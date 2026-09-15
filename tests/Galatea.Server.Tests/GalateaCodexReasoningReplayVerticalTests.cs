using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Exercises the production Codex adapter and raw journal with an in-memory
/// HTTP transport. No credentials are read and no network request can escape.
/// </summary>
public sealed class GalateaCodexReasoningReplayVerticalTests {
    private const string OldModel = "gpt-5.6-sol";
    private const string NewModel = "gpt-6-astra";
    private const string OldAnswer = "Visible answer from the previous model.";
    private const string NewAnswer = "Visible answer after switching models.";
    private const string ReasoningCanary = "SYNTHETIC_OLD_REASONING_CANARY";
    // Pinned from the previous Responses adapter, before projection identity
    // was introduced. It is intentionally not computed by the current factory.
    private const string PreviousCodexAdapterFingerprint =
        "sha256:8c256736bee867f3e135ff8a61b2d8a85438cae327f3299363cd03910f982fc0";
    private static readonly TimeSpan CompletionDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SwitchingModel_ReplaysNativeReasoningWithoutChangingStoredOrigin() {
        var factory = new CodexFixtureFactory();
        await using var host = CreateHost(factory);
        using HttpClient http = host.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        (GalateaHostService service, CharacterSessionHost session) = await GetSessionAsync(host);

        GalateaLiveTurn oldTurn = await StartAndWaitAsync(
            http, service, session, "First turn.", "test");
        Assert.Equal("completed", oldTurn.Status);
        SessionCompletedTurnProjection first = Assert.Single(
            session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
        OpenAIResponsesReasoningBlock oldReasoning = Assert.Single(
            first.TerminalAction.Message.Blocks.OfType<OpenAIResponsesReasoningBlock>());
        Assert.Equal(OldModel, oldReasoning.Origin.Model);
        string originalReasoningJson = oldReasoning.RawItemJson;

        GalateaLiveTurn newTurn = await StartAndWaitAsync(
            http, service, session, "Continue with the new model.", "astra");

        Assert.Equal("completed", newTurn.Status);
        Assert.Equal(SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase);
        string[] requests = factory.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        AssertSwitchedRequest(requests[1], originalReasoningJson);
        var completed = session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns;
        Assert.Equal(2, completed.Count);
        Assert.Equal(NewAnswer, completed[0].TerminalAction.Message.GetFlattenedText());
        // Cross-model replay never rewrites the provenance in raw history.
        OpenAIResponsesReasoningBlock retainedReasoning = Assert.Single(
            completed[1].TerminalAction.Message.Blocks
                .OfType<OpenAIResponsesReasoningBlock>());
        Assert.Equal(originalReasoningJson, retainedReasoning.RawItemJson);
        Assert.Equal(oldReasoning.Origin, retainedReasoning.Origin);
    }

    [Theory]
    [InlineData("AfterRequestPreparedCommitted", SessionDurableDispatchState.NotStarted)]
    [InlineData("AfterCompletionAttemptStartedCommitted",
        SessionDurableDispatchState.StartedOutcomeUncertain)]
    public async Task PreviousProjectionFrozenV7Request_ExplicitRestartUsesCurrentAdapter(
        string failpoint, SessionDurableDispatchState expectedDispatchState) {
        var factory = new CodexFixtureFactory();
        CompletionConnectionConfig connection = Connection("test", OldModel);
        await using var host = GalateaTestHost.CreateMissingSession(factory,
            DisabledGalateaUserMessageNormalizer.Instance, connections: [connection]);
        using var client = (OpenAICodexResponsesClient)factory.Create(connection);
        bool started = expectedDispatchState == SessionDurableDispatchState.StartedOutcomeUncertain;
        Assert.Equal(started ? "AfterCompletionAttemptStartedCommitted" : "AfterRequestPreparedCommitted", failpoint);
        EventAddress frozenHead = LegacyPreparedV7Fixture.CreatePending(host.SessionDirectory,
            connection, client, started, PreviousCodexAdapterFingerprint);
        SessionPreparedRequestReconstruction exact = GalateaRecapFixture.ReadLatestPrepared(host.SessionDirectory);
        Assert.Equal(2, exact.Manifest.Plan.ExactContextInputs.Length);
        using (var audit = SessionJournalEngine.OpenReadOnly(host.SessionDirectory)) {
            var events = new List<SessionJournalAuditEvent>();
            audit.ScanCheckedAuditEvents(events.Add);
            Assert.Equal(1, events.Single(entry => entry.Kind == SessionEventKind.SystemPromptSetup).BodySchemaVersion);
            Assert.Equal(1, events.Single(entry => entry.Kind == SessionEventKind.ObservationAccepted).BodySchemaVersion);
            Assert.Equal(7, events.Single(entry => entry.Kind == SessionEventKind.CompletionRequestPrepared).BodySchemaVersion);
        }
        Assert.Empty(factory.Requests);
        Assert.Equal(0, factory.CredentialReads);

        using HttpClient http = host.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        (GalateaHostService service, CharacterSessionHost session) = await GetSessionAsync(host);
        var frozen = Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(
            session.Engine.InspectRuntimeRecoveryRequirements());
        Assert.Equal(expectedDispatchState, frozen.DispatchState);

        using HttpResponseMessage accepted = await http.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(EventAddressTextCodec.Format(frozenHead),
                ConnectionId: null, RestartUncertainCompletion: true));
        GalateaLiveTurn recovered = await WaitForTurnAsync(accepted, service, session);

        Assert.Equal("completed", recovered.Status);
        Assert.Equal(SessionExecutionPhase.Idle, session.Engine.InspectExecutionBoundary().Phase);
        Assert.Single(factory.Requests);
        Assert.Equal(1, factory.CredentialReads);
        Assert.Equal(OldAnswer, session.Engine.ReadRecentCompletedTurns().RequireSnapshot()
            .Turns[0].TerminalAction.Message.GetFlattenedText());
        OpenAIResponsesReasoningBlock reasoning = Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot()
            .Turns[0].TerminalAction.Message.Blocks.OfType<OpenAIResponsesReasoningBlock>());
        Assert.Equal(OldModel, reasoning.Origin.Model);
        using JsonDocument rawReasoning = JsonDocument.Parse(reasoning.RawItemJson);
        Assert.Equal(ReasoningCanary, rawReasoning.RootElement.GetProperty("encrypted_content").GetString());
    }

    [Fact]
    public async Task FrozenStartedModelSwitch_RequiresExplicitRestartThenUsesProductionAdapter() {
        var factory = new CodexFixtureFactory();
        await using var host = CreateHost(factory);
        CompletionConnectionConfig oldConnection = Connection("test", OldModel);
        CompletionConnectionConfig newConnection = Connection("astra", NewModel);
        using var oldClient = (OpenAICodexResponsesClient)factory.Create(oldConnection);
        using var newClient = (OpenAICodexResponsesClient)factory.Create(newConnection);
        using (var engine = SessionJournalEngine.Open(host.SessionDirectory)) {
            ReconcileModel(engine, oldConnection);
            engine.UseRuntime(GalateaDurableRecoveryVerticalTests.CreateFixtureRuntime(
                oldConnection, oldClient) with { InputProjector = GalateaInputProjector.Instance });
            await engine.SendAsync(GalateaUserMessageEnvelope.Wrap("First turn."),
                CancellationToken.None);
            ReconcileModel(engine, newConnection);
        }
        EventAddress startedHead = await GalateaDurableRecoveryVerticalTests
            .CreateRecoveryBoundaryAsync(
                host.SessionDirectory, newConnection, newClient,
                "AfterCompletionAttemptStartedCommitted",
                SessionExecutionPhase.AwaitingCompletion,
                (engine, runtime) => BindRawOnlyRuntimeAsync(engine, runtime,
                    GalateaUserMessageEnvelope.Wrap("fixture observation")));
        Assert.Single(factory.Requests);

        using HttpClient http = host.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        (GalateaHostService service, CharacterSessionHost session) = await GetSessionAsync(host);
        using (HttpResponseMessage refused = await http.PostAsJsonAsync(
                   "/api/v1/characters/alice/chat/turns/resume",
                   new ResumeTurnRequest(EventAddressTextCodec.Format(startedHead),
                       ConnectionId: null, RestartUncertainCompletion: false))) {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            using JsonDocument body = JsonDocument.Parse(
                await refused.Content.ReadAsStringAsync());
            Assert.Equal("uncertain-completion-restart-required",
                body.RootElement.GetProperty("code").GetString());
        }
        Assert.Single(factory.Requests);
        Assert.Equal(startedHead, session.Engine.ReadCurrentHead());

        using HttpResponseMessage accepted = await http.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(EventAddressTextCodec.Format(startedHead),
                ConnectionId: null, RestartUncertainCompletion: true));
        GalateaLiveTurn recovered = await WaitForTurnAsync(accepted, service, session);

        Assert.Equal("completed", recovered.Status);
        Assert.Equal("astra", recovered.Options.ConnectionId);
        Assert.Equal(SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase);
        string[] requests = factory.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        AssertSwitchedRequest(requests[1]);
        Assert.Equal(NewAnswer,
            session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns[0]
                .TerminalAction.Message.GetFlattenedText());
    }

    [Fact]
    public async Task SameOriginUnsupportedCarrier_IsDurableKnownFailureWithoutHttpDispatch() {
        var factory = new CodexFixtureFactory();
        await using var host = CreateHost(factory);
        CompletionConnectionConfig connection = Connection("test", OldModel);
        using var productionClient = (OpenAICodexResponsesClient)factory.Create(connection);
        EventAddress failedHead;
        using (var engine = SessionJournalEngine.Open(host.SessionDirectory)) {
            ReconcileModel(engine, connection);
            engine.UseRuntime(GalateaDurableRecoveryVerticalTests.CreateFixtureRuntime(
                connection, new UnsupportedCarrierSeedClient()) with { InputProjector = GalateaInputProjector.Instance });
            await engine.SendAsync(GalateaUserMessageEnvelope.Wrap("Seed legacy carrier."),
                CancellationToken.None);
            await using IAsyncDisposable online = await BindRawOnlyRuntimeAsync(engine,
                GalateaDurableRecoveryVerticalTests.CreateFixtureRuntime(
                    connection, productionClient),
                GalateaUserMessageEnvelope.Wrap("Reject before transport."));

            SessionJournalTurnAbortedException rejected = await Assert.ThrowsAsync<
                SessionJournalTurnAbortedException>(() => engine.SendAsync(
                    GalateaUserMessageEnvelope.Wrap("Reject before transport."),
                    CancellationToken.None));

            Assert.Equal("openai.responses.invalid-reasoning-replay",
                rejected.Termination.ProviderReason);
            Assert.Equal(["adapter-validation=reasoning-replay"], rejected.Errors);
            failedHead = Assert.IsType<SessionRuntimeRecoveryRequirements
                .FailedTurnMustBeAbandoned>(engine.InspectRuntimeRecoveryRequirements())
                .FailedHead;
        }
        using (var reopened = SessionJournalEngine.OpenReadOnly(host.SessionDirectory)) {
            Assert.Equal(failedHead, Assert.IsType<SessionRuntimeRecoveryRequirements
                .FailedTurnMustBeAbandoned>(reopened.InspectRuntimeRecoveryRequirements())
                .FailedHead);
            var events = new List<SessionJournalAuditEvent>();
            reopened.ScanCheckedAuditEvents(events.Add);
            SessionJournalAuditEvent failure = Assert.Single(events,
                entry => entry.Kind == SessionEventKind.CompletionAttemptFailed);
            Assert.Equal(failedHead, failure.Address);
        }
        Assert.Empty(factory.Requests);
        Assert.Equal(0, factory.CredentialReads);

        using HttpClient http = host.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        CurrentTurnDto current = Assert.IsType<CurrentTurnDto>(
            await http.GetFromJsonAsync<CurrentTurnDto>("/api/v1/characters/alice/chat/turns/current"));
        Assert.Equal("idle", current.Status);
        Assert.False(current.RestartRequired);
        Assert.Null(current.RecoveryHead);
        Assert.Empty(factory.Requests);
    }

    private static GalateaTestHost CreateHost(CodexFixtureFactory factory) =>
        GalateaTestHost.Create(factory, DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test", OldModel), Connection("astra", NewModel)]);

    private static CompletionConnectionConfig Connection(string id, string model) => new(
        id, CodexSubscriptionCompletionClientFactory.ConnectionKind, model,
        CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
        CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress);

    private static void ReconcileModel(SessionJournalEngine engine,
        CompletionConnectionConfig connection) {
        EventAddress? head = engine.ReadCurrentHead();
        SessionGoverningSetup setup = engine.ResolveGoverningSetup(head!.Value);
        Assert.IsType<SessionDesiredSetupReconciliationResult.Ready>(
            engine.ReconcileDesiredSetup(head, new SessionDesiredSetup(
                connection.ModelId, connection.CompletionSurfaceId, setup.SystemPrompt)));
    }

    internal static async ValueTask<IAsyncDisposable> BindRawOnlyRuntimeAsync(
        SessionJournalEngine engine, SessionRuntime runtime, string pendingObservation) {
        RecapGridOnlineContextHandle online = Assert.IsType<RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(engine, new RejectingBatchExecutor(),
                RecapGridOnlineLimits.Production, new O200kBaseHistoryUnitLoadEstimator())).Handle;
        try {
            Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(
                await online.CatchUpMaintenanceAsync(pendingObservation));
            engine.UseRuntime(runtime with {
                ContextCandidateSource = online.CandidateSource,
                ContextLifecycle = online.Lifecycle,
                InputProjector = GalateaInputProjector.Instance
            });
            return online;
        }
        catch {
            await online.DisposeAsync();
            throw;
        }
    }

    private static async Task<(GalateaHostService, CharacterSessionHost)> GetSessionAsync(
        GalateaTestHost host) {
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        return (service, await service.GetSessionAsync("alice", CancellationToken.None));
    }

    private static async Task<GalateaLiveTurn> StartAndWaitAsync(HttpClient http,
        GalateaHostService service, CharacterSessionHost session, string text,
        string connectionId) {
        using HttpResponseMessage accepted = await http.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns", new ChatStreamRequest(text, connectionId));
        return await WaitForTurnAsync(accepted, service, session);
    }

    private static async Task<GalateaLiveTurn> WaitForTurnAsync(
        HttpResponseMessage accepted, GalateaHostService service, CharacterSessionHost session) {
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        StartTurnResponseDto started = Assert.IsType<StartTurnResponseDto>(
            await accepted.Content.ReadFromJsonAsync<StartTurnResponseDto>());
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId));
        await Assert.IsAssignableFrom<Task>(turn.RunTask).WaitAsync(CompletionDeadline);
        return turn;
    }

    private static void AssertSwitchedRequest(string body, string? originalReasoningJson = null) {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(NewModel, root.GetProperty("model").GetString());
        JsonElement reasoning = Assert.Single(root.GetProperty("input").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "reasoning");
        Assert.Equal(ReasoningCanary, reasoning.GetProperty("encrypted_content").GetString());
        if (originalReasoningJson is not null) {
            using JsonDocument original = JsonDocument.Parse(originalReasoningJson);
            Assert.True(JsonElement.DeepEquals(original.RootElement, reasoning));
        }
        Assert.Contains(root.GetProperty("input").EnumerateArray(), item =>
            item.GetProperty("type").GetString() == "message"
            && item.GetProperty("role").GetString() == "assistant"
            && item.GetProperty("content").EnumerateArray().Any(content =>
                content.GetProperty("text").GetString() == OldAnswer));
    }

    private sealed class CodexFixtureFactory : ICompletionClientFactory,
        ICodexSubscriptionCredentialProvider {
        private readonly CodexSubscriptionCredential _credential =
            CodexSubscriptionCredential.Create("synthetic-test-token", "synthetic-test-account",
                residency: null, expiresAt: null, stableGeneration: 1);
        private int _credentialReads;
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal int CredentialReads => Volatile.Read(ref _credentialReads);

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Assert.Equal(CodexSubscriptionCompletionClientFactory.ConnectionKind, connection.Kind);
            // Keep transport injection test-local: do not make a new production
            // public hook or read Codex CLI credentials just for this fixture.
            ConstructorInfo constructor = typeof(OpenAICodexResponsesClient).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                [typeof(ICodexSubscriptionCredentialProvider),
                    typeof(OpenAICodexResponsesClientOptions), typeof(HttpMessageHandler)],
                modifiers: null)!;
            return (ICompletionClient)constructor.Invoke([
                this, new OpenAICodexResponsesClientOptions {
                    ExpectedAccountFingerprint = _credential.AccountFingerprint,
                    ProductVersion = "test",
                    ReasoningEffort = connection.ReasoningEffort
                }, new FixtureHandler(Requests)]);
        }

        public ValueTask<CodexSubscriptionCredential> GetCredentialAsync(
            CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _credentialReads);
            return ValueTask.FromResult(_credential);
        }
    }

    private sealed class FixtureHandler(ConcurrentQueue<string> requests) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            requests.Enqueue(body);
            using JsonDocument document = JsonDocument.Parse(body);
            string model = document.RootElement.GetProperty("model").GetString()!;
            Assert.Contains(model, new[] { OldModel, NewModel });
            string reasoningEvent = model == OldModel
                ? Event(new {
                    type = "response.output_item.done",
                    item = new {
                        id = "rs_fixture",
                        type = "reasoning",
                        encrypted_content = ReasoningCanary
                    }
                })
                : string.Empty;
            string text = model == OldModel ? OldAnswer : NewAnswer;
            string stream = reasoningEvent
                + Event(new { type = "response.output_text.delta", delta = text })
                + Event(new { type = "response.completed" });
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            };
        }

        private static string Event(object payload) =>
            "data: " + JsonSerializer.Serialize(payload) + "\n\n";
    }

    private sealed class UnsupportedCarrierSeedClient : ICompletionClient {
        public string Name => "chatgpt.com";
        public string ApiSpecId => "openai-codex-responses-v2";

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            var invocation = new CompletionDescriptor(Name, ApiSpecId, request.ModelId);
            return Task.FromResult(new CompletionResult(new ActionMessage([
                new ActionBlock.TextReasoningBlock("Synthetic legacy carrier.", invocation),
                new ActionBlock.Text(OldAnswer)]), invocation));
        }
    }

    private sealed class RejectingBatchExecutor : IRecapCellBatchExecutor {
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(FrozenRowBatch batch,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "The raw-only fixture must not request any RecapGrid model call.");
    }
}
