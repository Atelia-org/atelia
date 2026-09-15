using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.CharacterMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaAdmissionRetryTests {
    private const string Endpoint = "/api/v1/characters/alice/agent/retry-admission";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrySettlesActualFailedExtractionWithoutNewTurnAndPreservesOtherBlock(bool replyFailed) {
        var completion = new Factory();
        await using var fixture = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test"), Connection("note")],
            selectableConnectionIds: ["test"], characterNoteExtractorConnectionId: "note",
            heartbeatCharacterIds: ["alice"]);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        CharacterSessionHost session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = host.StartTurn(session, "save a note", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
            await host.RunTurnAsync(session, turn, CancellationToken.None).WaitAsync(Deadline);
            host.FinishTurn(session, turn);
            turn.Complete();
            Assert.Equal("completed", turn.Status);
        }
        finally { session.TurnLock.Release(); }
        Assert.Equal(1, completion.MainCalls);
        await Assert.ThrowsAsync<GalateaTurnException>(() => coordinator.TryPulseAsync("alice", CancellationToken.None));
        Assert.True(session.AutomaticAdmissionFailed);
        var head = session.Engine.ReadCurrentHead();
        var cadence = session.AutonomyCadence.ProjectStatus();
        using HttpClient client = fixture.CreateClient();
        using var login = await GalateaTestHost.LoginAsync(client);
        using (var failed = await client.PostAsync(Endpoint, Json("{}"))) {
            Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
            string body = await failed.Content.ReadAsStringAsync();
            Assert.Contains("character-memory-extraction-unavailable", body, StringComparison.Ordinal);
            Assert.DoesNotContain("private provider detail", body, StringComparison.Ordinal);
        }
        Assert.True(session.AutomaticAdmissionFailed);
        Assert.Null(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
        Assert.Equal(head, session.Engine.ReadCurrentHead());

        await session.TurnLock.WaitAsync();
        try { session.AutomaticReplyFailed = replyFailed; }
        finally { session.TurnLock.Release(); }
        completion.FailExtraction = false;
        completion.GateExtraction = true;
        Task<HttpResponseMessage> retry = client.PostAsync(Endpoint, Json("{}"));
        await completion.Entered.Task.WaitAsync(Deadline);
        Assert.IsType<GalateaAutomaticTurnResult.Busy>(await coordinator.TryPulseAsync("alice", CancellationToken.None));
        Assert.IsType<GalateaAutomaticTurnResult.Busy>(await coordinator.RetryAdmissionAsync("alice", CancellationToken.None));
        completion.Release.SetResult();
        using (HttpResponseMessage success = await retry.WaitAsync(Deadline)) {
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
            Assert.Equal(replyFailed ? "blocked" : "waiting", body.RootElement.GetProperty("state").GetString());
            Assert.Equal(replyFailed ? "AUTOMATIC_REPLY_FAILED" : null, body.RootElement.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("admissionFailure").ValueKind);
        }
        Assert.False(session.AutomaticAdmissionFailed);
        Assert.Equal(replyFailed, session.AutomaticReplyFailed);
        Assert.Equal(head, session.Engine.ReadCurrentHead());
        Assert.Equal(cadence, session.AutonomyCadence.ProjectStatus());
        Assert.Equal(1, completion.MainCalls);
        Assert.Null(session.GetCurrentTurn());
        Assert.NotNull(session.CharacterMemoryReconciler.ReadPendingReceiptDelivery());
        var pod = global::Atelia.MemoPod.MemoPod.Open(session.Character.CharacterMemoryStateDir, CharacterNoteDefaultPodV1.PodId);
        Assert.Equal("Remember the blue door.", Assert.Single(pod.List()).ExactText);
        int extracted = completion.ExtractionCalls;
        using var again = await client.PostAsync(Endpoint, Json("{}"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(extracted, completion.ExtractionCalls);
        Assert.Equal(1, completion.MainCalls);
    }

    [Theory]
    [InlineData(false, false, HttpStatusCode.OK)]
    [InlineData(true, true, HttpStatusCode.ServiceUnavailable)]
    public async Task RetryRespectsEnrollmentAndMaintenanceWithoutAttaching(bool enrolled, bool maintenance, HttpStatusCode expected) {
        var completion = new Factory();
        await using var fixture = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance, maintenanceMode: maintenance,
            heartbeatCharacterIds: enrolled ? ["alice"] : []);
        using HttpClient client = fixture.CreateClient();
        using var anonymous = await client.PostAsync(Endpoint, Json("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var login = await GalateaTestHost.LoginAsync(client);
        using var response = await client.PostAsync(Endpoint, Json("{}"));
        Assert.Equal(expected, response.StatusCode);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        Assert.Null(host.ReadAttachedSession("alice"));
        Assert.Equal(0, completion.MainCalls);
    }

    [Fact]
    public async Task RetryRejectsUnexpectedInputAndDoesNotAttachOrClearInitializationFailure() {
        var completion = new Factory();
        await using var fixture = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance, heartbeatCharacterIds: ["alice"]);
        using HttpClient client = fixture.CreateClient();
        using var login = await GalateaTestHost.LoginAsync(client);
        foreach (string body in new[] { "null", "{\"connectionId\":\"test\"}", "{\"skip\":true}" }) {
            using var rejected = await client.PostAsync(Endpoint, Json(body));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        coordinator.BlockAfterFailure("alice");
        using var unavailable = await client.PostAsync(Endpoint, Json("{}"));
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Equal("blocked", coordinator.ReadStatus("alice").State);
        Assert.Null(coordinator.ReadStatus("alice").AdmissionFailure);
        Assert.Null(fixture.Factory.Services.GetRequiredService<GalateaHostService>().ReadAttachedSession("alice"));
    }

    [Fact]
    public async Task RetryDoesNotClearPendingTurnRecoveryOrAdvanceItsHead() {
        var completion = new Factory();
        await using var fixture = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance, heartbeatCharacterIds: ["alice"]);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        CharacterSessionHost session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            session.Engine.AppendObservation(GalateaUserMessageEnvelope.Wrap("pending user input"));
            GalateaAutomaticTurnCoordinator.RecordAdmissionFailure(session, new IOException());
        }
        finally { session.TurnLock.Release(); }
        var head = session.Engine.ReadCurrentHead();
        var result = Assert.IsType<GalateaAutomaticTurnResult.Blocked>(
            await coordinator.RetryAdmissionAsync("alice", CancellationToken.None));
        Assert.Equal("recovery-required", result.Code);
        Assert.True(session.AutomaticAdmissionFailed);
        Assert.Equal("RECOVERY_REQUIRED", coordinator.ReadStatus("alice").Code);
        var pulse = Assert.IsType<GalateaAutomaticTurnResult.Blocked>(
            await coordinator.TryPulseAsync("alice", CancellationToken.None));
        Assert.Equal("recovery-required", pulse.Code);
        Assert.Equal("RECOVERY_REQUIRED", coordinator.ReadStatus("alice").Code);
        Assert.Null(coordinator.ReadStatus("alice").AdmissionFailure);
        Assert.Equal(head, session.Engine.ReadCurrentHead());
        Assert.Equal(0, completion.MainCalls);
        Assert.Equal(0, completion.ExtractionCalls);
        Assert.Null(session.GetCurrentTurn());
    }

    [Fact]
    public async Task CancelledRetryKeepsOriginalFailure() {
        await using var fixture = GalateaTestHost.Create(new Factory(),
            DisabledGalateaUserMessageNormalizer.Instance, heartbeatCharacterIds: ["alice"]);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        CharacterSessionHost session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try { GalateaAutomaticTurnCoordinator.RecordAdmissionFailure(session, new IOException()); }
        finally { session.TurnLock.Release(); }
        GalateaAgentStatusDto before = coordinator.ReadStatus("alice");
        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RetryAdmissionAsync("alice", new CancellationToken(true)));
        Assert.True(session.AutomaticAdmissionFailed);
        Assert.Equal(before, coordinator.ReadStatus("alice"));
        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();
    }

    [Fact]
    public async Task RetryDoesNotClearFailureAfterShutdownBegins() {
        await using var fixture = GalateaTestHost.Create(new Factory(),
            DisabledGalateaUserMessageNormalizer.Instance, heartbeatCharacterIds: ["alice"]);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        CharacterSessionHost session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try { GalateaAutomaticTurnCoordinator.RecordAdmissionFailure(session, new IOException()); }
        finally { session.TurnLock.Release(); }
        host.BeginShutdown();
        var status = Assert.IsType<GalateaAutomaticTurnResult.Status>(await coordinator.RetryAdmissionAsync("alice", CancellationToken.None));
        Assert.Equal("stopping", status.Value.State);
        Assert.True(session.AutomaticAdmissionFailed);
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
    private static CompletionConnectionConfig Connection(string id) => new(
        id, "openai-chat", "model-a", "openai-chat/strict", "http://127.0.0.1:1/", ApiKey: "test-key");

    private sealed class Factory : ICompletionClientFactory {
        internal int MainCalls;
        internal int ExtractionCalls;
        internal bool FailExtraction = true;
        internal bool GateExtraction;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ICompletionClient Create(CompletionConnectionConfig connection) => new Client(this, connection.Id == "test");
        private sealed class Client(Factory factory, bool main) : ICompletionClient {
            public string Name => "admission-retry-test";
            public string ApiSpecId => "admission-retry-v1";
            public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
                CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
                ActionMessage message;
                if (main) {
                    Interlocked.Increment(ref factory.MainCalls);
                    message = new([new ActionBlock.Text("[Galatea] Please save this long-term Note: Remember the blue door.")]);
                }
                else if (request.PromptPrefix.OutputContract.Tools.Any(tool => tool.Name == CharacterNoteExtractor.ToolName)) {
                    Interlocked.Increment(ref factory.ExtractionCalls);
                    if (factory.GateExtraction) {
                        factory.Entered.TrySetResult();
                        await factory.Release.Task.WaitAsync(cancellationToken);
                    }
                    if (factory.FailExtraction) {
                        throw new TextExtractionException(TextExtractionFailureKind.CompletionOutputInvalid, "private provider detail");
                    }
                    message = new([new ActionBlock.ToolCall(new RawToolCall(CharacterNoteExtractor.ToolName,
                        "note", JsonSerializer.Serialize(new { text = "Remember the blue door." })))]);
                }
                else { message = new([]); }
                return new(message, CompletionDescriptor.From(this, request));
            }
        }
    }
}
