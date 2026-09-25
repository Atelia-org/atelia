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

    [Fact]
    public async Task UnrepresentableLayoutBlocksAdmissionWithoutCaptureOrGenerationRetry() {
        var completion = new Factory { FailExtraction = false, ReportLayoutProblem = true };
        await using var fixture = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test"), Connection("note")],
            connectionOptionIds: ["test"], characterNoteExtractorConnectionId: "note");
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = host.StartTurn(session, "save a note", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
        try {
            GalateaTurnException failure = await Assert.ThrowsAsync<GalateaTurnException>(
                () => host.RunTurnAsync(session, turn, CancellationToken.None).WaitAsync(Deadline));
            Assert.Equal("character-memory-unrepresentable-layout", failure.FailureReason);
            var extraction = Assert.IsType<TextExtractionException>(failure.InnerException);
            Assert.NotNull(extraction.ExtractionSource);
            Assert.Equal("alice", extraction.ExtractionSource.CharacterId);
            Assert.Equal(Atelia.SessionJournal.EventAddressTextCodec.Format(session.Engine.ReadCurrentHead()!.Value),
                extraction.ExtractionSource.SourceAction);
        }
        finally {
            host.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
        Assert.Equal(1, completion.ExtractionCalls);
        var head = session.Engine.ReadCurrentHead();
        using HttpClient client = fixture.CreateClient();
        using var login = await GalateaTestHost.LoginAsync(client);
        using var response = await client.PostAsync(Endpoint, Json("{}"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("character-memory-unrepresentable-layout", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(2, completion.ExtractionCalls); // One batch per explicit attempt, no generation retry.
        Assert.Equal(1, completion.MainCalls);
        Assert.Equal(head, session.Engine.ReadCurrentHead());
        Assert.Null(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrySettlesActualFailedExtractionWithoutNewTurnAndPreservesOtherBlock(bool replyFailed) {
        var completion = new Factory();
        var clock = new ManualTimeProvider();
        await using var fixture = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test"), Connection("note")],
            connectionOptionIds: ["test"], characterNoteExtractorConnectionId: "note",
            timeProvider: clock, autonomyCharacterIds: ["alice"]);
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
        var cadence = session.AutonomyCadence!.ProjectStatus();
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
        clock.Advance(TimeSpan.FromSeconds(30));
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
        Assert.Equal(cadence, session.AutonomyCadence!.ProjectStatus());
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
    public async Task RetryInspectsExistingZeroIntervalSessionButMaintenanceDoesNotAttach(bool enrolled, bool maintenance, HttpStatusCode expected) {
        var completion = new Factory();
        await using var fixture = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance, maintenanceMode: maintenance,
            autonomyCharacterIds: enrolled ? ["alice"] : []);
        using HttpClient client = fixture.CreateClient();
        using var anonymous = await client.PostAsync(Endpoint, Json("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var login = await GalateaTestHost.LoginAsync(client);
        using var response = await client.PostAsync(Endpoint, Json("{}"));
        Assert.Equal(expected, response.StatusCode);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        if (maintenance) { Assert.Null(host.ReadAttachedSession("alice")); }
        else { Assert.NotNull(host.ReadAttachedSession("alice")); }
        Assert.Equal(0, completion.MainCalls);
    }

    [Fact]
    public async Task RetryRejectsUnexpectedInputAndDoesNotAttachOrClearInitializationFailure() {
        var completion = new Factory();
        await using var fixture = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance, autonomyCharacterIds: ["alice"]);
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
            DisabledGalateaUserMessageNormalizer.Instance, autonomyCharacterIds: ["alice"]);
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
        Assert.Equal(head, session.Engine.ReadCurrentHead());
        Assert.Equal(0, completion.MainCalls);
        var pulse = Assert.IsType<GalateaAutomaticTurnResult.Started>(
            await coordinator.TryPulseAsync("alice", CancellationToken.None));
        Assert.Equal("recovery", pulse.Origin);
        await pulse.Turn.RunTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", pulse.Turn.Status);
        Assert.Null(coordinator.ReadStatus("alice").AdmissionFailure);
        Assert.NotEqual(head, session.Engine.ReadCurrentHead());
        Assert.Equal(1, completion.MainCalls);
        Assert.Equal(0, completion.ExtractionCalls);
        Assert.Null(session.GetCurrentTurn());
    }

    [Fact]
    public async Task CancelledRetryKeepsOriginalFailure() {
        await using var fixture = GalateaTestHost.Create(new Factory(),
            DisabledGalateaUserMessageNormalizer.Instance, autonomyCharacterIds: ["alice"]);
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
            DisabledGalateaUserMessageNormalizer.Instance, autonomyCharacterIds: ["alice"]);
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

    private sealed class ManualTimeProvider : TimeProvider {
        private long _timestamp;
        private DateTimeOffset _utcNow = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan elapsed) {
            _timestamp += elapsed.Ticks;
            _utcNow += elapsed;
        }
    }

    private sealed class Factory : ICompletionClientFactory {
        internal int MainCalls;
        internal int ExtractionCalls;
        internal bool FailExtraction = true;
        internal bool ReportLayoutProblem;
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
                    message = new([new ActionBlock.Text(factory.ReportLayoutProblem
                        ? "[Galatea] Please save this long-term Note: Remember the blue door. I close my notebook."
                        : "[Galatea]\nPlease save this long-term Note:\nRemember the blue door.")]);
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
                    message = factory.ReportLayoutProblem
                        ? new([new ActionBlock.ToolCall(new RawToolCall("report_extraction_problem", "layout",
                            "{\"reason\":\"unrepresentable_layout\"}"))])
                        : request.TailMessages.OfType<ToolResultsMessage>().Any()
                        ? new([])
                        : new([new ActionBlock.ToolCall(new RawToolCall(CharacterNoteExtractor.ToolName,
                            "note", JsonSerializer.Serialize(new { textStartLine = 3, textEndLine = 3 })))]);
                }
                else { message = new([]); }
                return new(message, CompletionDescriptor.From(this, request));
            }
        }
    }
}
