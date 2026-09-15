using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Legacy Prepared bytes through the real Host, Registry, Journal and
/// Anthropic adapter. The only provider transport is an in-memory handler.</summary>
public sealed class GalateaAnthropicPreparedV7RecoveryTests {
    private const string Model = "claude-opus-4-6";
    private const string Answer = "Recovered through the current Anthropic adapter.";

    [Theory]
    [InlineData("AfterRequestPreparedCommitted", false, "old-adapter-before-output-policy")]
    [InlineData("AfterRequestPreparedCommitted", false, "old-adapter-after-output-policy")]
    [InlineData("AfterCompletionAttemptStartedCommitted", true, "old-adapter-before-output-policy")]
    [InlineData("AfterCompletionAttemptStartedCommitted", true, "old-adapter-after-output-policy")]
    public async Task LegacyPrepared_RecoversWithModelsFallback_ThenNewFormatSurvivesColdAudit(
        string failpoint, bool started, string oldAdapter) {
        Assert.Equal(started ? "AfterCompletionAttemptStartedCommitted" : "AfterRequestPreparedCommitted", failpoint);
        var factory = new AnthropicFixtureFactory();
        var connection = new CompletionConnectionConfig("test", "anthropic", Model,
            "anthropic", "https://synthetic-anthropic.invalid/", ApiKey: "synthetic-key");
        await using var lab = GalateaScenarioLab.CreateLegacy("anthropic-v7-" + (started ? "started" : "prepared"),
            factory, connections: [connection]);
        await lab.StopAsync();
        EventAddress frozenHead;
        using (var fixtureClient = (IDisposable)factory.Create(connection)) {
            frozenHead = LegacyPreparedV7Fixture.CreatePending(lab.SessionDirectory,
                connection, (ICompletionClient)fixtureClient, started, oldAdapter);
        }
        SessionPreparedRequestReconstruction frozen = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        Assert.Empty(factory.Requests);
        Assert.Empty(factory.LogicalRequests);
        Assert.Equal(2, frozen.Manifest.Plan.ExactContextInputs.Length);
        using (var audit = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            var history = new List<SessionJournalAuditEvent>();
            audit.ScanCheckedAuditEvents(history.Add);
            Assert.Equal(1, history.Single(entry => entry.Kind == SessionEventKind.SystemPromptSetup).BodySchemaVersion);
            Assert.Equal(1, history.Single(entry => entry.Kind == SessionEventKind.ObservationAccepted).BodySchemaVersion);
            Assert.Equal(7, history.Single(entry => entry.Kind == SessionEventKind.CompletionRequestPrepared).BodySchemaVersion);
        }

        await lab.ReopenAsync(factory);
        using (HttpClient http = lab.Host.CreateClient()) {
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var service = lab.Host.Factory.Services.GetRequiredService<GalateaHostService>();
            CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
            var required = Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(
                session.Engine.InspectRuntimeRecoveryRequirements());
            Assert.Equal(started ? SessionDurableDispatchState.StartedOutcomeUncertain
                : SessionDurableDispatchState.NotStarted, required.DispatchState);
            if (started) {
                using HttpResponseMessage refused = await http.PostAsJsonAsync("/api/v1/characters/alice/chat/turns/resume",
                    new ResumeTurnRequest(EventAddressTextCodec.Format(frozenHead), null,
                        RestartUncertainCompletion: false));
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Contains("uncertain-completion-restart-required",
                    await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                Assert.Equal(frozenHead, session.Engine.ReadCurrentHead());
                Assert.Empty(factory.Requests);
                Assert.Empty(factory.LogicalRequests);
            }
            using HttpResponseMessage accepted = await http.PostAsJsonAsync("/api/v1/characters/alice/chat/turns/resume",
                new ResumeTurnRequest(EventAddressTextCodec.Format(frozenHead), null,
                    RestartUncertainCompletion: started));
            GalateaLiveTurn recovered = await GalateaRecapFixture.WaitAsync(accepted, service, session);
            AssertCompleted(recovered);
            Assert.Equal(Answer, Assert.Single(session.Engine.ReadRecentCompletedTurns()
                .RequireSnapshot().Turns).TerminalAction.Message.GetFlattenedText());
            Assert.Equal(frozen.CanonicalBytes, Assert.Single(factory.LogicalRequests));
        }
        RequestCapture[] calls = factory.Requests.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(("GET", "/v1/models/" + Model), (calls[0].Method, calls[0].Path));
        Assert.Equal(("POST", "/v1/messages"), (calls[1].Method, calls[1].Path));
        using (JsonDocument body = JsonDocument.Parse(calls[1].Body!)) {
            Assert.Equal(Model, body.RootElement.GetProperty("model").GetString());
            Assert.Equal(128000, body.RootElement.GetProperty("max_tokens").GetInt32());
        }
        await lab.StopAsync();
        SessionPreparedRequestReconstruction recoveredRequest = GalateaRecapFixture.ReadLatestPrepared(lab.SessionDirectory);
        Assert.Equal(frozen.SourcePreparedAddress, recoveredRequest.SourcePreparedAddress);
        Assert.Equal(frozen.CanonicalBytes, recoveredRequest.CanonicalBytes);
        Assert.Equal(frozen.Manifest.Commitment, recoveredRequest.Manifest.Commitment);

        // A new Host must read the completed v7 before it can append current v9.
        await lab.ReopenAsync(factory);
        using (HttpClient http = lab.Host.CreateClient()) {
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var service = lab.Host.Factory.Services.GetRequiredService<GalateaHostService>();
            CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
            using HttpResponseMessage accepted = await http.PostAsJsonAsync("/api/v1/characters/alice/chat/turns",
                new ChatStreamRequest("Fresh turn after legacy recovery.", "test"));
            AssertCompleted(await GalateaRecapFixture.WaitAsync(accepted, service, session));
        }
        await lab.StopAsync();
        using (var engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
            var events = new List<SessionJournalAuditEvent>();
            SessionJournalAuditScanResult audit = engine.ScanCheckedAuditEvents(events.Add);
            Assert.Equal(2, audit.Diagnostics.PreparedReconstructionCount);
            Assert.Equal(new[] { 7, 9 }, events.Where(entry => entry.Kind == SessionEventKind.CompletionRequestPrepared)
                .Select(entry => entry.BodySchemaVersion).ToArray());
            SessionSelectedLineageAuditSession selected = engine.BeginSelectedLineageAudit();
            while (!selected.IsCaptureComplete) { _ = selected.ReadNextPage(maxEventCount: 3); }
            Assert.Equal(audit.CapturedHead, selected.Complete().Capture.CapturedHead);
            Assert.Equal(2, engine.ReadRecentCompletedTurns().RequireSnapshot().Turns.Count);
        }
        Assert.Equal(2, factory.LogicalRequests.Count);
        await lab.CompleteAsync();
    }

    private static void AssertCompleted(GalateaLiveTurn turn) {
        using GalateaTurnSubscription subscription = turn.Subscribe();
        string errors = string.Join("\n", subscription.ReplayFrames
            .Where(frame => frame.EventName == "error")
            .Select(frame => Encoding.UTF8.GetString(frame.Utf8.Span)));
        Assert.True(turn.Status == "completed", $"Turn status={turn.Status}; errors={errors}");
    }

    private sealed record RequestCapture(string Method, string Path, string? Body);

    private sealed class AnthropicFixtureFactory : ICompletionClientFactory {
        internal ConcurrentQueue<RequestCapture> Requests { get; } = new();
        internal ConcurrentQueue<byte[]> LogicalRequests { get; } = new();

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Assert.Equal("anthropic", connection.Kind);
            var http = new HttpClient(new Handler(Requests)) { BaseAddress = new Uri(connection.BaseAddress) };
            return new RecordingClient(new AnthropicClient(connection.ApiKey, http), http, LogicalRequests);
        }
    }

    private sealed class RecordingClient(AnthropicClient inner, HttpClient http,
        ConcurrentQueue<byte[]> logicalRequests) : ICompletionClient, IDisposable {
        public string Name => inner.Name;
        public string ApiSpecId => inner.ApiSpecId;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            logicalRequests.Enqueue(SessionRequestCanonicalizer.Canonicalize(request));
            return inner.StreamCompletionAsync(request, observer, cancellationToken);
        }

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionInvocationOptions invocationOptions, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            logicalRequests.Enqueue(SessionRequestCanonicalizer.Canonicalize(request));
            return inner.StreamCompletionAsync(request, invocationOptions, observer, cancellationToken);
        }

        public void Dispose() => http.Dispose();
    }

    private sealed class Handler(ConcurrentQueue<RequestCapture> requests) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) {
            string path = request.RequestUri!.AbsolutePath;
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Enqueue(new RequestCapture(request.Method.Method, path, body));
            if (request.Method == HttpMethod.Get) {
                Assert.Equal("/v1/models/" + Model, path);
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") };
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/messages", path);
            string stream = """
                event: message_start
                data: {"type":"message_start","message":{}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Recovered through the current Anthropic adapter."}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

                event: message_stop
                data: {"type":"message_stop"}

                """;
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(stream + "\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
