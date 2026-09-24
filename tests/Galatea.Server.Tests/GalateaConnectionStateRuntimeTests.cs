using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaConnectionStateRuntimeTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string DressAction = "[Galatea] 她换上裙装。\n[状态摘要] 当前穿着裙装。";
    private const string WorkAction = "[Galatea] 她换上裤装。\n[状态摘要] 当前穿着裤装。";

    [Fact]
    public async Task CompletedActionsSwitchNextTurnAndRepeatedOrInvalidExtractionKeepsSelection() {
        var completion = new ScriptedCompletion();
        completion.MainActions.Enqueue(DressAction);
        completion.MainActions.Enqueue(DressAction);
        completion.MainActions.Enqueue(WorkAction);
        completion.MainActions.Enqueue(WorkAction);
        completion.ExtractionReplies.Enqueue(new("dress", "穿着裙装"));
        completion.ExtractionReplies.Enqueue(new("dress", "穿着裙装"));
        completion.ExtractionReplies.Enqueue(new("other", "穿着裤装"));
        completion.ExtractionReplies.Enqueue(null);
        await using var fixture = CreateFixture(completion);
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        using HttpClient http = fixture.CreateClient();
        await LoginAsync(http);

        await SendAndFinishAsync(http, service, session, "第一回合");
        GalateaConnectionStateSnapshot switched = service.CaptureConnectionState("alice", "dress");
        Assert.Equal("dress", switched.RuntimeOverrideConnectionId);
        Assert.Equal("dress", switched.EffectiveConnectionId);
        Assert.Equal("test", switched.LastChange!.PreviousConnectionId);
        Assert.Equal("dress", switched.LastChange.ConnectionId);
        Assert.Equal("生活状态", switched.LastChange.Name);
        Assert.Equal("穿着裙装", switched.LastChange.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(switched.LastChange.SourceActionAddress));

        await SendAndFinishAsync(http, service, session, "第二回合");
        GalateaConnectionStateSnapshot repeated = service.CaptureConnectionState("alice", "dress");
        Assert.Equal(switched.LastChange, repeated.LastChange);

        await SendAndFinishAsync(http, service, session, "第三回合");
        Assert.Equal("dress", service.CaptureConnectionState("alice", "dress").EffectiveConnectionId);
        await SendAndFinishAsync(http, service, session, "第四回合");
        Assert.Equal("dress", service.CaptureConnectionState("alice", "dress").EffectiveConnectionId);

        Assert.Equal(["test", "dress", "dress", "dress"], completion.MainConnectionIds.ToArray());
        Assert.Equal(4, completion.ExtractionCalls);
        SessionCompletedTurnProjection[] turns = session.Engine.ReadRecentCompletedTurns(4)
            .RequireSnapshot().Turns.Reverse().ToArray();
        Assert.Equal(4, turns.Length);
        JsonElement firstSnapshot = turns[0].ObservationContent.JsonValue.GetProperty("connectionState");
        Assert.Equal("test", firstSnapshot.GetProperty("effectiveConnectionId").GetString());
        Assert.Equal("test", firstSnapshot.GetProperty("turnConnectionId").GetString());
        JsonElement nextSnapshot = turns[1].ObservationContent.JsonValue.GetProperty("connectionState");
        Assert.Equal("dress", nextSnapshot.GetProperty("effectiveConnectionId").GetString());
        Assert.Equal("dress", nextSnapshot.GetProperty("turnConnectionId").GetString());
        Assert.Equal(switched.LastChange.SourceActionAddress,
            nextSnapshot.GetProperty("lastChange").GetProperty("sourceActionAddress").GetString());
    }

    [Fact]
    public async Task DiagnosticTurnReportsItsActualConnectionWhileRuntimeChoiceRemainsEffective() {
        var completion = new ScriptedCompletion();
        completion.MainActions.Enqueue(DressAction);
        completion.MainActions.Enqueue(DressAction);
        completion.ExtractionReplies.Enqueue(new("dress", "穿着裙装"));
        completion.ExtractionReplies.Enqueue(null);
        await using var fixture = CreateFixture(completion);
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        using HttpClient http = fixture.CreateClient();
        await LoginAsync(http);
        await SendAndFinishAsync(http, service, session, "切换");
        await SendAndFinishAsync(http, service, session, "诊断", diagnosticConnectionId: "test");

        Assert.Equal(["test", "test"], completion.MainConnectionIds.ToArray());
        JsonElement observation = session.Engine.ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single().ObservationContent.JsonValue;
        JsonElement snapshot = observation.GetProperty("connectionState");
        Assert.Equal("dress", snapshot.GetProperty("runtimeOverrideConnectionId").GetString());
        Assert.Equal("dress", snapshot.GetProperty("effectiveConnectionId").GetString());
        Assert.Equal("test", snapshot.GetProperty("turnConnectionId").GetString());
        Assert.Equal("dress", service.CaptureConnectionState("alice", "dress").EffectiveConnectionId);
    }

    [Fact]
    public async Task AcceptedSnapshotStaysFixedAcrossOverrideChangeAndInboundMail() {
        var completion = new ScriptedCompletion();
        completion.MainActions.Enqueue(WorkAction);
        completion.MainActions.Enqueue(DressAction);
        completion.ExtractionReplies.Enqueue(null);
        completion.ExtractionReplies.Enqueue(new("dress", "穿着裙装"));
        await using var fixture = CreateFixture(completion);
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn accepted = service.StartTurn(session, "接纳时仍是默认连接",
                new GalateaTurnOptions("test"), GalateaDelegateTestConfiguration.PlayerSender);
            Assert.Equal("test", accepted.Options.ConnectionState!.EffectiveConnectionId);
            Assert.Equal("test", accepted.Options.ConnectionState.TurnConnectionId);

            service.SetRuntimeConnectionOverride("alice", "dress");
            Assert.Equal("dress", service.CaptureConnectionState("alice", "dress").EffectiveConnectionId);
            await service.RunTurnAsync(session, accepted, CancellationToken.None).WaitAsync(Deadline);
            service.FinishTurn(session, accepted);
            Assert.Equal("completed", accepted.Status);
            JsonElement first = session.Engine.ReadRecentCompletedTurns(1).RequireSnapshot()
                .Turns.Single().ObservationContent.JsonValue.GetProperty("connectionState");
            Assert.Equal("test", first.GetProperty("effectiveConnectionId").GetString());
            Assert.Equal("test", first.GetProperty("turnConnectionId").GetString());

            GalateaLiveTurn inbound = service.StartInboundMailTurn(session,
                MailboxMessage.CreateInbound(session.Character.CharacterName,
                    "outside", null, "这里有一封信。"),
                new GalateaTurnOptions("dress"),
                injectedBy: GalateaDelegateTestConfiguration.PlayerSender);
            Assert.Equal("dress", inbound.Options.ConnectionState!.EffectiveConnectionId);
            await service.RunTurnAsync(session, inbound, CancellationToken.None).WaitAsync(Deadline);
            service.FinishTurn(session, inbound);
            Assert.Equal("completed", inbound.Status);
            JsonElement mail = session.Engine.ReadRecentCompletedTurns(1).RequireSnapshot()
                .Turns.Single().ObservationContent.JsonValue;
            Assert.Equal("inbound-mail", mail.GetProperty("kind").GetString());
            Assert.Equal("dress", mail.GetProperty("connectionState")
                .GetProperty("turnConnectionId").GetString());
            Assert.Equal("dress", service.CaptureConnectionState("alice", "dress").EffectiveConnectionId);
        }
        finally { session.TurnLock.Release(); }
        Assert.Equal(["test", "dress"], completion.MainConnectionIds.ToArray());
        Assert.Equal(2, completion.ExtractionCalls);
    }

    [Fact]
    public async Task TimedOutExtractionLeavesDefaultSelection() {
        var completion = new ScriptedCompletion { BlockExtraction = true };
        completion.MainActions.Enqueue(DressAction);
        await using var fixture = CreateFixture(completion);
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        service.ConnectionStateExtractionDeadlineForTest = TimeSpan.FromMilliseconds(50);
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        using HttpClient http = fixture.CreateClient();
        await LoginAsync(http);
        await SendAndFinishAsync(http, service, session, "超时");
        Assert.Equal(1, completion.ExtractionCalls);
        Assert.Null(service.CaptureConnectionState("alice", "test").RuntimeOverrideConnectionId);
        Assert.Equal("test", service.CaptureConnectionState("alice", "test").EffectiveConnectionId);
    }

    [Fact]
    public async Task ColdRestartClearsOverrideAndPopClearsLaterSwitch() {
        var beforeRestart = new ScriptedCompletion();
        beforeRestart.MainActions.Enqueue(DressAction);
        beforeRestart.ExtractionReplies.Enqueue(new("dress", "穿着裙装"));
        await using var first = CreateFixture(beforeRestart, deleteFilesOnDispose: false);
        GalateaHostService firstService = first.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost firstSession = await firstService.GetSessionAsync("alice", CancellationToken.None);
        using (HttpClient http = first.CreateClient()) {
            await LoginAsync(http);
            await SendAndFinishAsync(http, firstService, firstSession, "重启前");
        }
        Assert.Equal("dress", firstService.CaptureConnectionState("alice", "dress").EffectiveConnectionId);
        await first.DisposeAsync();

        var afterRestart = new ScriptedCompletion();
        afterRestart.MainActions.Enqueue(DressAction);
        afterRestart.ExtractionReplies.Enqueue(new("dress", "穿着裙装"));
        await using var reopened = first.CreateRestarted(afterRestart,
            DisabledGalateaUserMessageNormalizer.Instance, new NoDispatchTransport());
        GalateaHostService service = reopened.Factory.Services.GetRequiredService<GalateaHostService>();
        Assert.Null(service.CaptureConnectionState("alice", "test").RuntimeOverrideConnectionId);
        Assert.Equal("test", service.CaptureConnectionState("alice", "test").EffectiveConnectionId);
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        using HttpClient restartedHttp = reopened.CreateClient();
        await LoginAsync(restartedHttp);
        await SendAndFinishAsync(restartedHttp, service, session, "重建状态");
        Assert.Equal(["test"], afterRestart.MainConnectionIds.ToArray());
        Assert.Equal("dress", service.CaptureConnectionState("alice", "dress").EffectiveConnectionId);

        RecentTurnsResponseDto recent = (await restartedHttp.GetFromJsonAsync<RecentTurnsResponseDto>(
            "/api/v1/characters/alice/recent-turns"))!;
        Assert.NotNull(recent.RewindLatestToken);
        using HttpResponseMessage pop = await restartedHttp.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/pop-latest",
            new PopLatestTurnRequestDto(recent.RewindLatestToken));
        Assert.Equal(HttpStatusCode.OK, pop.StatusCode);
        Assert.Null(service.CaptureConnectionState("alice", "test").RuntimeOverrideConnectionId);
        Assert.Equal("test", service.CaptureConnectionState("alice", "test").EffectiveConnectionId);
    }

    private static GalateaTestHost CreateFixture(ScriptedCompletion completion,
        bool deleteFilesOnDispose = true) {
        GalateaTestHost fixture = GalateaTestHost.Create(
            completion, DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: deleteFilesOnDispose,
            connections: [Connection("test"), Connection("dress"), Connection("extract")],
            connectionOptionIds: ["test", "dress"],
            characterConnectionStateExtractorConnectionId: "extract");
        JsonObject root = JsonNode.Parse(File.ReadAllText(fixture.ConfigPath))!.AsObject();
        JsonArray options = root["characters"]![0]!["connectionOptions"]!.AsArray();
        options[0]!["name"] = "工作状态";
        options[0]!["trigger"] = "穿着裤装";
        options[1]!["name"] = "生活状态";
        options[1]!["trigger"] = "穿着裙装";
        File.WriteAllText(fixture.ConfigPath, root.ToJsonString());
        return fixture;
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id, "openai-chat", "model-a", "openai-chat/strict",
        "http://localhost:8000/", ApiKey: "test-key");

    private static async Task LoginAsync(HttpClient http) {
        using HttpResponseMessage response = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task SendAndFinishAsync(HttpClient http, GalateaHostService service,
        CharacterSessionHost session, string message, string? diagnosticConnectionId = null) {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns",
            new ChatStreamRequest(message, diagnosticConnectionId));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto started = Assert.IsType<StartTurnResponseDto>(
            await response.Content.ReadFromJsonAsync<StartTurnResponseDto>());
        GalateaLiveTurn turn = service.FindTurn(session, started.TurnId)!;
        await turn.RunTask!.WaitAsync(Deadline);
        Assert.Equal("completed", turn.Status);
    }

    private sealed record ExtractionReply(string ConnectionId, string Evidence);

    private sealed class NoDispatchTransport : IGalateaDurableDelegateTransport {
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected delegate call.");
        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected delegate call.");
        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected delegate call.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedCompletion : ICompletionClientFactory {
        internal ConcurrentQueue<string> MainActions { get; } = new();
        internal ConcurrentQueue<ExtractionReply?> ExtractionReplies { get; } = new();
        internal ConcurrentQueue<string> MainConnectionIds { get; } = new();
        internal int ExtractionCalls;
        internal bool BlockExtraction;

        public ICompletionClient Create(CompletionConnectionConfig connection) => new Client(this, connection.Id);

        private sealed class Client(ScriptedCompletion owner, string connectionId) : ICompletionClient {
            public string Name => "connection-state-runtime-test";
            public string ApiSpecId => "openai-chat-v1";

            public async Task<CompletionResult> StreamCompletionAsync(
                CompletionRequest request, CompletionStreamObserver? observer,
                CancellationToken cancellationToken = default) {
                cancellationToken.ThrowIfCancellationRequested();
                ActionMessage result;
                if (connectionId == "extract") {
                    Assert.Contains(request.PromptPrefix.OutputContract.Tools,
                        tool => tool.Name == CharacterConnectionStateExtractor.ToolName);
                    Interlocked.Increment(ref owner.ExtractionCalls);
                    if (owner.BlockExtraction) {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    Assert.True(owner.ExtractionReplies.TryDequeue(out ExtractionReply? reply));
                    result = reply is null ? new ActionMessage([]) :
                        new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                            CharacterConnectionStateExtractor.ToolName,
                            "state-call",
                            JsonSerializer.Serialize(new {
                                connectionId = reply.ConnectionId,
                                evidence = reply.Evidence
                            })))]);
                }
                else {
                    owner.MainConnectionIds.Enqueue(connectionId);
                    Assert.True(owner.MainActions.TryDequeue(out string? action));
                    string visibleAction = Assert.IsType<string>(action);
                    observer?.OnTextDelta(visibleAction);
                    result = new ActionMessage([new ActionBlock.Text(visibleAction)]);
                }
                return new CompletionResult(result, CompletionDescriptor.From(this, request));
            }
        }
    }
}
