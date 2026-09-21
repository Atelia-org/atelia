using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Real process death, real production host/client/storage, synthetic external
/// Responses service. This does not simulate power loss or prove external
/// exactly-once effects; the first provider result is intentionally unknown.
/// </summary>
[Trait("Category", "GalateaLab")]
public sealed class GalateaProcessCrashRehearsalTests(ITestOutputHelper output) {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task KilledAfterProviderReceivesRequest_AutomaticallyResumes_ThenColdReopensIdle() {
        await using var provider = await GalateaLabCrashResponsesServer.StartAsync();
        await using var lab = GalateaScenarioLab.Create("process-crash-started",
            new NeverCreateClientFactory(),
            connections: [new CompletionConnectionConfig(
                "test", "openai-responses", GalateaLabCrashResponsesServer.Model,
                "openai-responses", provider.BaseAddress.AbsoluteUri,
                ApiKey: "synthetic-lab-key")],
            reportArtifact: path => output.WriteLine("Retained synthetic lab: " + path));
        string configPath = lab.Host.ConfigPath;
        // Dispose the never-started WebApplicationFactory; only the production
        // child is permitted to open this fixture's application state.
        await lab.StopAsync();

        await using (GalateaLabServerProcess first = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = first.CreateClient();
            await LoginAsync(http);
            using HttpResponseMessage accepted = await GalateaLabAdmission.PostFreshAsync(http,
                new ChatStreamRequest(GalateaLabCrashResponsesServer.UserMessage));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            await provider.FirstReceived.WaitAsync(Deadline);
            // Observing external HTTP establishes that Prepared was committed;
            // killing here bypasses every production shutdown/Dispose hook.
            await first.KillAsync();
        }
        await provider.FirstDisconnected.WaitAsync(Deadline);

        EventAddress startedHead;
        using (SessionJournalEngine offline = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            var frozen = Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(
                offline.InspectRuntimeRecoveryRequirements());
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, frozen.Phase);
            Assert.NotEqual(default, frozen.SourcePreparedAddress);
            startedHead = frozen.CapturedHead!.Value;
            var events = new List<SessionJournalAuditEvent>();
            offline.ScanCheckedAuditEvents(events.Add);
            Assert.Single(events, item => item.Kind == SessionEventKind.ObservationAccepted);
            Assert.DoesNotContain(events, item => item.Kind == SessionEventKind.CompletionAttemptStarted);
            Assert.DoesNotContain(events, item => item.Kind == SessionEventKind.AgentActionProduced);
        }

        provider.AuthorizeRestart();
        await using (GalateaLabServerProcess restarted = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = restarted.CreateClient();
            await LoginAsync(http);
            // EOF follows terminal publication, but the runner releases its
            // lock just afterwards. Wait for that final ownership handoff;
            // a single immediate status read could legitimately say running.
            await WaitForIdleAsync(http);
            provider.AssertComplete();
            await restarted.KillAsync();
        }

        using (SessionJournalEngine offline = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            Assert.Equal(SessionExecutionPhase.Idle, offline.InspectExecutionBoundary().Phase);
            SessionCompletedTurnProjection turn = Assert.Single(
                offline.ReadRecentCompletedTurns().RequireSnapshot().Turns);
            Assert.Equal(GalateaLabCrashResponsesServer.Answer,
                turn.RequireTerminalAction().Message.GetFlattenedText());
            var events = new List<SessionJournalAuditEvent>();
            offline.ScanCheckedAuditEvents(events.Add);
            Assert.Single(events, item => item.Kind == SessionEventKind.ObservationAccepted);
            Assert.DoesNotContain(events, item => item.Kind == SessionEventKind.CompletionAttemptStarted);
            Assert.Single(events, item => item.Kind == SessionEventKind.CompletionRequestPrepared);
            Assert.Single(events, item => item.Kind == SessionEventKind.AgentActionProduced);
        }
        provider.AssertComplete();
        await lab.CompleteAsync();
    }

    private static async Task LoginAsync(HttpClient http) {
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    private static async Task WaitForIdleAsync(HttpClient http) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (true) {
            CurrentTurnDto current = Assert.IsType<CurrentTurnDto>(
                await http.GetFromJsonAsync<CurrentTurnDto>(
                    "/api/v1/characters/alice/chat/turns/current", deadline.Token));
            if (current.Status == "idle") { return; }
            Assert.Contains(current.Status, new[] { "running", "recovery-required" });
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    private sealed class NeverCreateClientFactory : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection) =>
            throw new InvalidOperationException("The parent lab host must never dispatch completion.");
    }
}
