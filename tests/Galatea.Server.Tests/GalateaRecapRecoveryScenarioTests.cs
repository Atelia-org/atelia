using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Real nonempty Online selection with existing journal failpoints;
/// deterministic cold reopen, not a process kill or old-version migration.</summary>
[Trait("Category", "GalateaLab")]
public sealed class GalateaRecapRecoveryScenarioTests(ITestOutputHelper output) {
    [Theory]
    [InlineData(nameof(SessionJournalFailpoint.AfterRequestPreparedCommitted), false, false)]
    [InlineData("LegacyStarted", true, false)]
    [InlineData(nameof(SessionJournalFailpoint.AfterRequestPreparedCommitted), false, true)]
    [InlineData("LegacyStarted", true, true)]
    public async Task AdoptedNonemptyRecap_SemanticRecoverySkipsMaintenanceThenFreshTurnProgresses(
        string failpointName, bool restartRequired, bool resetStore) {
        SessionJournalFailpoint failpoint = SessionJournalFailpoint.AfterRequestPreparedCommitted;
        Assert.Equal(restartRequired ? "LegacyStarted" : nameof(SessionJournalFailpoint.AfterRequestPreparedCommitted), failpointName);
        CompletionConnectionConfig main = Connection("test", "recap-lab-main");
        CompletionConnectionConfig recap = Connection("recap-maintainer", "recap-lab-helper");
        var seed = new GalateaRecapFixture.Factory(1, expectedMainCalls: 2);
        await using var lab = GalateaRecapFixture.CreateLab(
            "nonempty-recap-recovery-" + (restartRequired ? "started" : "prepared"),
            seed, main, recap, output.WriteLine);
        string routesPath = Path.Combine(Path.GetDirectoryName(lab.Host.ConfigPath)!, "recap-grid-routes.json");

        await GalateaRecapFixture.SeedAsync(lab, seed);
        await lab.StopAsync();
        seed.AssertComplete();
        SessionPreparedRequestReconstruction seedRequest = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        GalateaRecapFixture.AssertAdopted(seedRequest, 1);
        Assert.Equal(seedRequest.CanonicalBytes, seed.MainRequests.Last());
        AssertCells(lab.SessionDirectory, 1);

        var boundaryFactory = new GalateaRecapFixture.Factory(2, expectedMainCalls: 0, expectedRecapCalls: 4);
        EventAddress frozenHead = await FreezeAsync(lab.SessionDirectory, routesPath,
            main, recap, boundaryFactory,
            GalateaRecapGridDefaultPolicy.ForCharacter(new("Galatea")),
            failpoint, restartRequired);
        boundaryFactory.AssertComplete();
        SessionPreparedRequestReconstruction frozen = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        GalateaRecapFixture.AssertAdopted(frozen, 3);
        AssertCells(lab.SessionDirectory, 3);
        SessionJournalAuditEvent[] frozenAudit = ReadAudit(lab.SessionDirectory);
        SessionJournalAuditEvent frozenPrepared = frozenAudit.Last(entry => entry.Kind == SessionEventKind.CompletionRequestPrepared);
        Assert.Equal(3, frozenAudit.Count(entry => entry.Kind == SessionEventKind.ObservationAccepted));
        Assert.Equal(3, frozenAudit.Count(entry => entry.Kind == SessionEventKind.CompletionRequestPrepared));
        if (resetStore) {
            // Only this stopped, lab-owned repository is reset. The semantic
            // Prepared retains selected cell contents after their source store vanishes.
            RecapGridStoreInfo before = ReadStoreInfo(lab.SessionDirectory);
            Assert.True(before.CellCount > 0);
            RecapGridStorePhysicalWitness witness = Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(
                RecapGridStoreMaintenance.PrepareReset(lab.SessionDirectory)).Witness;
            RecapGridStoreResetResult.Reset reset = Assert.IsType<RecapGridStoreResetResult.Reset>(
                RecapGridStoreMaintenance.Reset(lab.SessionDirectory, witness));
            Assert.NotEqual(before.Identity.InstanceId, reset.Identity.InstanceId);
            AssertEmptyStore(lab.SessionDirectory);
        }
        SortedDictionary<string, string> frozenDerived = SnapshotDerived(lab.SessionDirectory);
        Assert.NotEmpty(frozenDerived);

        // A wholly new factory owns the restarted host. Any maintainer call is
        // immediately rejected; no callback or online handle survives the close.
        var recovery = new GalateaRecapFixture.Factory(3, expectedRecapCalls: 0);
        await lab.ReopenAsync(recovery);
        using (HttpClient http = lab.Host.CreateClient()) {
            http.Timeout = GalateaRecapFixture.Deadline;
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            (GalateaHostService service, CharacterSessionHost session) = await GalateaRecapFixture.SessionAsync(lab);
            var requirement = Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(
                session.Engine.InspectRuntimeRecoveryRequirements());
            Assert.NotEqual(default, requirement.SourcePreparedAddress);
            using HttpResponseMessage accepted = await http.PostAsJsonAsync("/api/v1/characters/alice/chat/turns/resume",
                new ResumeTurnRequest(EventAddressTextCodec.Format(frozenHead), null));
            GalateaLiveTurn resumed = await GalateaRecapFixture.WaitAsync(accepted, service, session);
            Assert.Equal("completed", resumed.Status);
        }
        await lab.StopAsync();
        recovery.AssertComplete();
        Assert.Equal(frozen.CanonicalBytes, Assert.Single(recovery.MainRequests));
        SessionPreparedRequestReconstruction reopenedRequest = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        Assert.Equal(frozen.SourcePreparedAddress, reopenedRequest.SourcePreparedAddress);
        Assert.Equal(frozen.CanonicalBytes, reopenedRequest.CanonicalBytes);
        Assert.Equal(JsonSerializer.Serialize(frozen.Manifest.Plan.SemanticContributions),
            JsonSerializer.Serialize(reopenedRequest.Manifest.Plan.SemanticContributions));
        IReadOnlyList<SessionRequestCommitment> attempts = GalateaRecapFixture.ReadAttemptCommitments(
            lab.SessionDirectory, frozen.SourcePreparedAddress!.Value);
        Assert.Equal(restartRequired ? 1 : 0, attempts.Count);
        Assert.All(attempts, commitment => Assert.Equal(
            SessionRequestCanonicalizer.CreateCommitment(reopenedRequest.Request), commitment));
        GalateaRecapFixture.AssertAdopted(reopenedRequest, 3);
        SessionJournalAuditEvent[] recoveredAudit = ReadAudit(lab.SessionDirectory);
        Assert.Equal(frozenPrepared, recoveredAudit.Single(entry => entry.Address == frozenPrepared.Address));
        Assert.Equal(3, recoveredAudit.Count(entry => entry.Kind == SessionEventKind.ObservationAccepted));
        Assert.Equal(3, recoveredAudit.Count(entry => entry.Kind == SessionEventKind.CompletionRequestPrepared));
        Assert.Equal(frozenDerived, SnapshotDerived(lab.SessionDirectory));
        if (resetStore) {
            AssertEmptyStore(lab.SessionDirectory);
        }
        else {
            AssertCells(lab.SessionDirectory, 3);
        }
        using (var reopened = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            Assert.Equal(SessionExecutionPhase.Idle, reopened.InspectExecutionBoundary().Phase);
        }

        // Fresh work advances the retained Store through generations 4/5, or
        // rebuilds all five rows from FirstRow when the old Store was discarded.
        var fresh = new GalateaRecapFixture.Factory(resetStore ? 1 : 4,
            expectedRecapCalls: resetStore ? 10 : 4);
        await lab.ReopenAsync(fresh);
        await GalateaRecapFixture.RunFreshAsync(lab, "Fresh observation after nonempty recap recovery.");
        await lab.StopAsync();
        fresh.AssertComplete();
        SessionPreparedRequestReconstruction freshRequest = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        GalateaRecapFixture.AssertAdopted(freshRequest, 5);
        Assert.Equal(freshRequest.CanonicalBytes, Assert.Single(fresh.MainRequests));
        Assert.NotEqual(frozen.SourcePreparedAddress, freshRequest.SourcePreparedAddress);
        AssertCells(lab.SessionDirectory, 5);
        using (var final = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            Assert.Equal(SessionExecutionPhase.Idle, final.InspectExecutionBoundary().Phase);
            var turns = final.ReadRecentCompletedTurns().RequireSnapshot().Turns;
            Assert.Equal(4, turns.Count);
            Assert.Contains("Fresh observation after nonempty recap recovery.", GalateaRecapFixture.ReadPlayerText(turns[0].ObservationContent),
                StringComparison.Ordinal);
        }
        Assert.Equal(4, ReadAudit(lab.SessionDirectory).Count(entry => entry.Kind == SessionEventKind.ObservationAccepted));
        await lab.CompleteAsync();
    }

    private static CompletionConnectionConfig Connection(string id, string model) => new(
        id, "openai-responses", model, "openai-responses", "http://127.0.0.1:1/", ApiKey: "synthetic-key");

    private static async Task<EventAddress> FreezeAsync(string repository, string routesPath,
        CompletionConnectionConfig main, CompletionConnectionConfig recap,
        GalateaRecapFixture.Factory factory,
        GalateaRecapGridDefaultPolicy defaultPolicy,
        SessionJournalFailpoint failpoint, bool legacyStarted) {
        ICompletionClient client = factory.Create(main);
        CompletionDispatchIdentity identity = CompletionDispatchIdentityFactory.Create(main, client);
        var runtime = new SessionRuntime(client, CompletionTarget: new SessionCompletionTargetIdentity(
            identity.ConnectionId, identity.Kind, identity.ConnectionFingerprint),
            InputProjector: GalateaInputProjector.Instance);
        EventAddress head;
        {
            using var timeout = new CancellationTokenSource(GalateaRecapFixture.Deadline);
            using var engine = SessionJournalEngine.OpenForTest(repository, runtime, new SessionJournalTestHooks(failpoint));
            await using RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
                () => RecapGridRouteManifest.DecodeCanonical(File.ReadAllBytes(routesPath)),
                CompletionConnectionConfigLoader.NormalizeAndValidate(new CompletionConnectionsFileConfig(
                    [main, recap], main.Id)), factory, inputProjector: GalateaInputProjector.Instance);
            await using RecapGridOnlineContextHandle online = Assert.IsType<RecapGridOnlineOpenResult.Opened>(
                RecapGridOnlineFactory.Open(engine, completion.Executor,
                    defaultPolicy.Target, RecapGridOnlineLimits.Production,
                    new O200kBaseHistoryUnitLoadEstimator())).Handle;
            SessionInputContent observation = GalateaObservationContent.CreatePlayerAction(
                GalateaDelegateTestConfiguration.PlayerSender,
                "Frozen nonempty derived-context observation.", DateTimeOffset.UnixEpoch.AddDays(1));
            RecapGridOnlinePassResult pass = await online.CatchUpMaintenanceAsync(observation, timeout.Token);
            Assert.True(pass is RecapGridOnlinePassResult.Ready,
                $"Expected exact Ready; got {pass.GetType().Name}, mainCalls={factory.MainCalls}, recapCalls={factory.RecapCalls}.");
            engine.UseRuntime(runtime with {
                ContextCandidateSource = online.CandidateSource, ContextLifecycle = online.Lifecycle
            });
            SessionJournalFailpointException failure = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync(observation, timeout.Token));
            Assert.Equal(failpoint, failure.Failpoint);
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion,
                engine.InspectExecutionBoundary().Phase);
            head = engine.ReadCurrentHead()!.Value;
        }
        return legacyStarted ? LegacyPreparedV7Fixture.AppendStarted(repository, head) : head;
    }

    private static void AssertCells(string repository, int generation) {
        var cells = GalateaRecapFixture.ReadHeadCells(repository);
        Assert.Equal(GalateaRecapFixture.World(generation), cells.World.Content);
        Assert.Equal(GalateaRecapFixture.Autobiography(generation), cells.Autobiography.Content);
    }

    private static RecapGridStoreInfo ReadStoreInfo(string repository) =>
        Assert.IsType<RecapGridStoreInspectResult.Available>(RecapGridStoreMaintenance.Inspect(repository)).Info;

    private static void AssertEmptyStore(string repository) {
        RecapGridStoreInfo info = ReadStoreInfo(repository);
        Assert.Equal(0, info.CellCount);
        Assert.Equal(0, info.RowViewCount);
        Assert.Equal(0, info.FulfilledViewCount);
    }

    private static SessionJournalAuditEvent[] ReadAudit(string repository) {
        using var engine = SessionJournalEngine.OpenReadOnly(repository);
        var events = new List<SessionJournalAuditEvent>();
        engine.ScanCheckedAuditEvents(events.Add);
        return events.ToArray();
    }

    private static SortedDictionary<string, string> SnapshotDerived(string repository) => new(
        Directory.GetFiles(repository, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetRelativePath(repository, path).StartsWith("derived" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.GetRelativePath(repository, path).StartsWith("control" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(repository, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))), StringComparer.Ordinal);
}
