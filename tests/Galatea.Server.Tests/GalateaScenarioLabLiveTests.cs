using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Opt-in synthetic Host + Codex + durable cold-reopen canary, never a live user repo.</summary>
public sealed class GalateaScenarioLabLiveTests(ITestOutputHelper output) {
    private const string Model = "gpt-5.6-luna";

    [Fact]
    [Trait("Category", "GalateaLabLive")]
    public async Task LiveE2E_LunaTwoTurnsAcrossColdReopen() {
        if (Environment.GetEnvironmentVariable("ATELIA_RUN_GALATEA_LAB_LIVE") != "1") {
            return;
        }
        string authPath = RequiredPath("ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE");
        string reportPath = RequiredPath("ATELIA_GALATEA_LAB_LIVE_REPORT");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using var report = new FileStream(reportPath, options);
        int completedTurns = 0;
        try {
            var provider = new CodexCliAuthFileCredentialProvider(authPath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            CodexSubscriptionCredential credential = await provider.GetCredentialAsync(deadline.Token);
            var factory = new BudgetedFactory(new CodexSubscriptionCompletionClientFactory(
                provider, credential.AccountFingerprint, originator: "atelia-galatea-lab",
                maxConcurrentRequests: 1, productVersion: "galatea-lab-v1"));
            var connection = new CompletionConnectionConfig(
                "test", CodexSubscriptionCompletionClientFactory.ConnectionKind, Model,
                CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
                CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress);
            await using var lab = GalateaScenarioLab.Create(
                "luna-cold-reopen-live", factory, connections: [connection],
                reportArtifact: output.WriteLine);

            await RunTurnAsync(lab.Host, "计算 (137 × 29) + (83 × 17)，简短回答结果。", deadline.Token);
            completedTurns++;
            await lab.StopAsync();
            string[] originalNative;
            using (var engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
                originalNative = engine.ReadRecentCompletedTurns().RequireSnapshot().Turns
                    .Single().TerminalAction.Message.Blocks.OfType<OpenAIResponsesReasoningBlock>()
                    .Select(block => block.RawItemJson).ToArray();
                Assert.NotEmpty(originalNative);
                Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
            }
            await WriteAsync(new { stage = "first-turn-stopped", completedTurns, factory.Calls,
                nativeItems = originalNative.Length, phase = "Idle" });

            await lab.ReopenAsync(factory);
            Assert.Equal(1, factory.Calls);
            await RunTurnAsync(lab.Host, "再检查一次刚才的算式，简短回答结果。", deadline.Token);
            completedTurns++;
            await lab.StopAsync();
            using (var engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
                var turns = engine.ReadRecentCompletedTurns().RequireSnapshot().Turns;
                Assert.Equal(2, turns.Count);
                Assert.Equal(originalNative, turns[^1].TerminalAction.Message.Blocks
                    .OfType<OpenAIResponsesReasoningBlock>().Select(block => block.RawItemJson).ToArray());
                Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
                _ = engine.ScanCheckedAuditEvents(_ => { });
            }
            Assert.Equal(2, factory.Calls);
            await WriteAsync(new { stage = "summary", success = true, model = Model,
                completedTurns, factory.Calls, phase = "Idle", source = "synthetic",
                liveUserStateTouched = false, nativeHistoryRetained = true });
            await lab.CompleteAsync();
        }
        catch (Exception error) {
            await WriteAsync(new { stage = "summary", success = false, model = Model,
                completedTurns, errorType = error.GetType().Name });
            // No provider body, account identity, credentials or opaque reasoning in test output.
            throw new Xunit.Sdk.XunitException(
                $"Galatea live canary failed ({error.GetType().Name}); inspect metadata report and retained lab artifact.");
        }

        async Task WriteAsync<T>(T row) {
            await JsonSerializer.SerializeAsync(report, row);
            await report.WriteAsync("\n"u8.ToArray());
            await report.FlushAsync();
        }
    }

    private static async Task RunTurnAsync(GalateaTestHost host, string text, CancellationToken ct) {
        using HttpClient http = host.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var service = host.Factory.Services.GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync("alice", ct);
        using HttpResponseMessage accepted = await http.PostAsJsonAsync(
            "/api/v1/chat/turns", new ChatStreamRequest(text, "test"), ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        StartTurnResponseDto started = (await accepted.Content.ReadFromJsonAsync<StartTurnResponseDto>(ct))!;
        GalateaLiveTurn turn = service.FindTurn(session, started.TurnId)!;
        await turn.RunTask!.WaitAsync(ct);
        Assert.Equal("completed", turn.Status);
        Assert.Equal(SessionExecutionPhase.Idle, session.Engine.InspectExecutionBoundary().Phase);
        Assert.NotEmpty(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns[0]
            .TerminalAction.Message.GetFlattenedText());
    }

    private static string RequiredPath(string name) {
        string? value = Environment.GetEnvironmentVariable(name);
        return value is not null && Path.IsPathFullyQualified(value)
            ? value : throw new InvalidOperationException($"{name} must specify an absolute path.");
    }

    private sealed class BudgetedFactory(ICompletionClientFactory inner) : ICompletionClientFactory {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Assert.Equal(Model, connection.ModelId);
            Assert.Equal(CodexSubscriptionCompletionClientFactory.ConnectionKind, connection.Kind);
            return new BudgetedClient(inner.Create(connection), this);
        }

        private sealed class BudgetedClient(ICompletionClient inner, BudgetedFactory owner)
            : ICompletionClient, IDisposable {
            public string Name => inner.Name;
            public string ApiSpecId => inner.ApiSpecId;
            public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
                CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
                if (Interlocked.Increment(ref owner._calls) > 2) {
                    throw new InvalidOperationException("Live canary permits exactly two completion invocations.");
                }
                return await inner.StreamCompletionAsync(request, observer, cancellationToken);
            }
            public void Dispose() => (inner as IDisposable)?.Dispose();
        }
    }
}
