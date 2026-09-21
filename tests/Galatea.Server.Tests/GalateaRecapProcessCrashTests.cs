using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Formal rolling recap, production Responses wire projection and real process
/// death. Recovery is checked cold before a separately started fresh turn is
/// allowed to advance the recipe. No live provider, artificial production
/// failpoint, or automatic scheduler is involved in the crash trigger.
/// </summary>
[Trait("Category", "GalateaLab")]
public sealed class GalateaRecapProcessCrashTests(ITestOutputHelper output) {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task NonemptyRecap_HardKill_RecoversSemanticPlan_ThenFreshTurnAdvancesRecap() {
        await using var provider = await GalateaLabRecapResponsesServer.StartAsync();
        var seedFactory = new GalateaRecapFixture.Factory(generation: 1, expectedMainCalls: 2);
        await using var lab = GalateaRecapFixture.CreateLab("recap-process-crash", seedFactory,
            Connection(GalateaRecapFixture.MainConnectionId, GalateaLabRecapResponsesServer.MainModel, provider),
            Connection(GalateaRecapFixture.RecapConnectionId, GalateaLabRecapResponsesServer.RecapModel, provider),
            reportArtifact: output.WriteLine);
        string configPath = lab.Host.ConfigPath;
        await GalateaRecapFixture.SeedAsync(lab, seedFactory);
        seedFactory.AssertComplete();
        await lab.StopAsync();
        GalateaRecapFixture.AssertAdopted(GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory), 1);
        using (var engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            AssertNoRawMarkers(engine, expectedTurns: 2);
        }
        string configDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath)));

        await using (GalateaLabServerProcess first = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = first.CreateClient();
            await LoginAsync(http);
            using HttpResponseMessage accepted = await GalateaLabAdmission.PostFreshAsync(http,
                new ChatStreamRequest(GalateaLabRecapResponsesServer.CrashMessage));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            // Observation and Action seal separate rows in this target-1
            // fixture: generations 2/3 require exactly four routed calls.
            // Main must adopt generation 3 in the actual formal carriers.
            await provider.FirstReceived.WaitAsync(Deadline);
            await first.KillAsync();
        }
        await provider.FirstDisconnected.WaitAsync(Deadline);
        provider.AssertHeld();

        SessionPreparedRequestReconstruction frozen = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        GalateaRecapFixture.AssertAdopted(frozen, generation: 3);
        DerivedIdentity frozenDerived = ReadDerivedIdentity(lab.SessionDirectory, generation: 3);
        EventAddress startedHead;
        EventAddress[] frozenObservations;
        using (var engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            var recovery = Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(
                engine.InspectRuntimeRecoveryRequirements());
            Assert.NotEqual(default, recovery.SourcePreparedAddress);
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, recovery.Phase);
            startedHead = recovery.CapturedHead!.Value;
            IReadOnlyList<SessionJournalAuditEvent> events = ReadAudit(engine);
            AssertCounts(events, observations: 3, prepared: 3, started: 0, actions: 2);
            frozenObservations = events.Where(item => item.Kind == SessionEventKind.ObservationAccepted)
                .Select(item => item.Address).ToArray();
            Assert.Equal(frozenObservations[^1], frozen.RawEndInclusive);
        }

        provider.AssertHeld();
        provider.AuthorizeRecovery();
        await using (GalateaLabServerProcess recovery = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = recovery.CreateClient();
            await LoginAsync(http);
            await WaitForIdleAsync(http);
            provider.AssertRecovered();
            await recovery.KillAsync();
        }

        // Check the recovery-only interval with all owners stopped, before a
        // fresh turn is allowed to legitimately advance recap cells/selection.
        SessionPreparedRequestReconstruction recovered = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        GalateaRecapFixture.AssertAdopted(recovered, generation: 3);
        Assert.Equal(frozen.SourcePreparedAddress, recovered.SourcePreparedAddress);
        Assert.Equal(frozen.RawEndInclusive, recovered.RawEndInclusive);
        Assert.Equal(frozen.CanonicalBytes, recovered.CanonicalBytes);
        Assert.True(frozen.Manifest.Plan.SemanticContributions.SequenceEqual(recovered.Manifest.Plan.SemanticContributions));
        IReadOnlyList<SessionRequestCommitment> attempts = GalateaRecapFixture.ReadAttemptCommitments(
            lab.SessionDirectory, frozen.SourcePreparedAddress!.Value);
        Assert.Empty(attempts);
        Assert.All(attempts, commitment => Assert.Equal(
            SessionRequestCanonicalizer.CreateCommitment(recovered.Request), commitment));
        Assert.Equal(frozenDerived, ReadDerivedIdentity(lab.SessionDirectory, generation: 3));
        EventAddress recoveredHead;
        using (var engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
            recoveredHead = engine.ReadCurrentHead()!.Value;
            IReadOnlyList<SessionJournalAuditEvent> events = ReadAudit(engine);
            AssertCounts(events, observations: 3, prepared: 3, started: 0, actions: 3);
            Assert.Equal(frozenObservations, events.Where(item => item.Kind == SessionEventKind.ObservationAccepted)
                .Select(item => item.Address));
            SessionCompletedTurnProjection resumed = Assert.Single(engine.ReadRecentCompletedTurns().RequireSnapshot().Turns,
                turn => turn.ObservationAddress == frozen.RawEndInclusive);
            Assert.Equal(GalateaLabRecapResponsesServer.RecoveryAnswer,
                resumed.RequireTerminalAction().Message.GetFlattenedText());
            AssertNoRawMarkers(engine, expectedTurns: 3);
        }
        provider.AssertRecovered();

        await using (GalateaLabServerProcess fresh = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = fresh.CreateClient();
            await LoginAsync(http);
            await WaitForColdIdleAsync(http, provider);
            RecentTurnsResponseDto recent = Assert.IsType<RecentTurnsResponseDto>(
                await http.GetFromJsonAsync<RecentTurnsResponseDto>("/api/v1/characters/alice/recent-turns"));
            Assert.Equal(EventAddressTextCodec.Format(recoveredHead), recent.RewindLatestToken);
            provider.AssertRecovered();
            provider.AuthorizeFresh();
            using HttpResponseMessage accepted = await GalateaLabAdmission.PostFreshAsync(http,
                new ChatStreamRequest(GalateaLabRecapResponsesServer.FreshMessage));
            await WaitForTurnAsync(http, accepted);
            provider.AssertComplete();
            await fresh.KillAsync();
        }
        SessionPreparedRequestReconstruction next = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        GalateaRecapFixture.AssertAdopted(next, generation: 5);
        Assert.NotEqual(frozen.SourcePreparedAddress, next.SourcePreparedAddress);
        DerivedIdentity nextDerived = ReadDerivedIdentity(lab.SessionDirectory, generation: 5);
        Assert.NotEqual(frozenDerived.World, nextDerived.World);
        Assert.NotEqual(frozenDerived.Autobiography, nextDerived.Autobiography);
        using (var engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
            AssertCounts(ReadAudit(engine), observations: 4, prepared: 4, started: 0, actions: 4);
            SessionCompletedTurnProjection latest = engine.ReadRecentCompletedTurns().RequireSnapshot().Turns[0];
            Assert.Equal(GalateaLabRecapResponsesServer.FreshAnswer, latest.RequireTerminalAction().Message.GetFlattenedText());
            Assert.Contains(GalateaLabRecapResponsesServer.FreshMessage, GalateaRecapFixture.ReadPlayerText(latest.ObservationContent), StringComparison.Ordinal);
            AssertNoRawMarkers(engine, expectedTurns: 4);
        }
        Assert.Equal(configDigest, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath))));
        await provider.DisposeAsync();
        provider.AssertComplete();
        await lab.CompleteAsync();
    }

    private static CompletionConnectionConfig Connection(string id, string model,
        GalateaLabRecapResponsesServer provider) => new(id, "openai-responses", model, "openai-responses",
        provider.BaseAddress.AbsoluteUri, ApiKey: GalateaLabRecapResponsesServer.ApiKey);

    private static IReadOnlyList<SessionJournalAuditEvent> ReadAudit(SessionJournalEngine engine) {
        var events = new List<SessionJournalAuditEvent>();
        engine.ScanCheckedAuditEvents(events.Add);
        return events;
    }

    private static void AssertCounts(IReadOnlyList<SessionJournalAuditEvent> events,
        int observations, int prepared, int started, int actions) {
        Assert.Equal(observations, events.Count(item => item.Kind == SessionEventKind.ObservationAccepted));
        Assert.Equal(prepared, events.Count(item => item.Kind == SessionEventKind.CompletionRequestPrepared));
        Assert.Equal(started, events.Count(item => item.Kind == SessionEventKind.CompletionAttemptStarted));
        Assert.Equal(actions, events.Count(item => item.Kind == SessionEventKind.AgentActionProduced));
        Assert.DoesNotContain(events, item => item.Kind is SessionEventKind.CompletionAttemptFailed
            or SessionEventKind.ToolExecutionStarted or SessionEventKind.ToolResultObserved);
    }

    private static void AssertNoRawMarkers(SessionJournalEngine engine, int expectedTurns) {
        IReadOnlyList<SessionCompletedTurnProjection> turns = engine.ReadRecentCompletedTurns().RequireSnapshot().Turns;
        Assert.Equal(expectedTurns, turns.Count);
        foreach (SessionCompletedTurnProjection turn in turns) {
            foreach (string text in new[] { System.Text.Encoding.UTF8.GetString(turn.ObservationContent.ToUtf8Json()), turn.RequireTerminalAction().Message.GetFlattenedText() }) {
                Assert.DoesNotContain("GALATEA_LAB_WORLD_RECAP_V", text, StringComparison.Ordinal);
                Assert.DoesNotContain("GALATEA_LAB_AUTOBIOGRAPHY_RECAP_V", text, StringComparison.Ordinal);
            }
        }
    }

    private sealed record DerivedIdentity(ControlHeadRef Control, string World, string Autobiography);
    private static DerivedIdentity ReadDerivedIdentity(string repository, int generation) {
        RefId refId;
        using (var engine = SessionJournalEngine.OpenReadOnly(repository)) { refId = engine.BranchRefId; }
        using RecapGridControlReaderHandle control = Assert.IsType<RecapGridControlReaderOpenResult.Opened>(
            RecapGridControlFactory.OpenReader(repository, refId)).Handle;
        ControlHeadRef head = Assert.IsType<RecapGridControlSnapshotResult.Available>(control.Reader.ReadSnapshot())
            .Snapshot.Head;
        var cells = GalateaRecapFixture.ReadHeadCells(repository);
        Assert.Equal(GalateaRecapFixture.World(generation), cells.World.Content);
        Assert.Equal(GalateaRecapFixture.Autobiography(generation), cells.Autobiography.Content);
        return new(head, cells.World.Id.Value, cells.Autobiography.Id.Value);
    }

    private static async Task LoginAsync(HttpClient http) {
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    private static async Task WaitForTurnAsync(HttpClient http, HttpResponseMessage accepted) {
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var turn = Assert.IsType<StartTurnResponseDto>(await accepted.Content.ReadFromJsonAsync<StartTurnResponseDto>());
        using HttpResponseMessage stream = await http.GetAsync($"/api/v1/characters/alice/chat/turns/{turn.TurnId}/events");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        await WaitForIdleAsync(http);
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

    private static async Task WaitForColdIdleAsync(HttpClient http, GalateaLabRecapResponsesServer provider) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (true) {
            CurrentTurnDto current = Assert.IsType<CurrentTurnDto>(await http.GetFromJsonAsync<CurrentTurnDto>(
                "/api/v1/characters/alice/chat/turns/current", deadline.Token));
            // A zero-interval startup inspection can hold TurnLock without a
            // live turn. Do not accept a new/recovered turn as mere startup.
            Assert.Null(current.TurnId);
            Assert.Null(current.RecoveryHead);
            provider.AssertRecovered(); // Cold attach must add zero calls.
            if (current.Status == "idle") { return; }
            Assert.Equal("running", current.Status);
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }
}
