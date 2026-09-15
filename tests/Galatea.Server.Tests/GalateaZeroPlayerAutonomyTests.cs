using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaZeroPlayerAutonomyTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string MailAction = "[Galatea] I sent Bob this message: hello Bob. I sent Codex this task: inspect the garden.";
    private const string CodexFinal = "Codex finished inspecting the garden.";

    [Fact]
    public async Task TwoCharactersWithoutPlayersOrBrowser_HeartbeatDeliversInternalMailAndConsumesCodexReply() {
        var clock = new GalateaLabClock();
        var completion = new ScriptedCompletion();
        var transport = new ScriptedTransport(completion.BobReceived.Task);
        await using GalateaTestHost fixture = GalateaTestHost.Create(
            completion, DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test"), Connection("helper")],
            selectableConnectionIds: ["test"],
            outboundMailExtractorConnectionId: "helper", delegateTransport: transport,
            timeProvider: clock, heartbeatCharacterIds: ["alice"],
            enableServerAgentHostedService: true);
        JsonNode config = JsonNode.Parse(File.ReadAllText(fixture.ConfigPath))!;
        JsonObject bob = config["characters"]![0]!.DeepClone().AsObject();
        bob["id"] = "bob";
        bob["name"] = "Bob";
        bob["heartbeatEnabled"] = false;
        bob["sessionProvisioning"] = "create-if-missing";
        bob["sessionDir"] = Path.Combine(fixture.RootDirectory, "bob-session");
        bob["delegationStateDir"] = Path.Combine(fixture.RootDirectory, "bob-delegation");
        bob["characterMemoryStateDir"] = Path.Combine(fixture.RootDirectory, "bob-memory");
        bob["homeDir"] = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.ConfigPath)!, "homes", "bob")).FullName;
        config["characters"]!.AsArray().Add(bob);
        config["players"] = new JsonArray();
        File.WriteAllText(fixture.ConfigPath, config.ToJsonString());

        // Starting the real hosted services needs neither an HTTP client nor a
        // synthetic Player. Bob is attached later only because mail arrives.
        IServiceProvider services = fixture.Factory.Services;
        Assert.Empty(services.GetRequiredService<GalateaConfig>().Players);
        GalateaHostService host = services.GetRequiredService<GalateaHostService>();
        GalateaAutomaticTurnCoordinator coordinator = services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        await UntilAsync(() => coordinator.ReadStatus("alice").State == "waiting");
        Assert.Null(host.ReadAttachedSession("bob"));
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(10));
        await completion.HeartbeatReceived.Task.WaitAsync(Deadline);
        await UntilAsync(() => host.ReadAttachedSession("alice")!.GetCurrentTurn() is null);
        CharacterSessionHost source = host.ReadAttachedSession("alice")!;
        GalateaAgentStatusDto sourceStatus = coordinator.ReadStatus("alice");
        Assert.True(sourceStatus.State == "waiting", JsonSerializer.Serialize(new {
            status = sourceStatus, helperCalls = completion.HelperCalls
        }));
        GalateaDelegationStateSnapshot captured = source.DelegationHandle!.Store.ReadSnapshot();
        Assert.Equal(2, captured.Mails.Count);
        Assert.Equal("bob", Assert.Single(captured.InternalMailOutboxes).TargetCharacterId);
        // The relay may have observed a temporarily busy target on its first
        // signal. Its real one-second fallback also needs this synthetic tick.
        clock.Advance(TimeSpan.FromSeconds(1));
        try {
            await completion.BobReceived.Task.WaitAsync(Deadline);
        }
        catch (TimeoutException) {
            GalateaDelegationStateSnapshot stalled = source.DelegationHandle!.Store.ReadSnapshot();
            CharacterSessionHost? target = host.ReadAttachedSession("bob");
            throw new Xunit.Sdk.XunitException("Internal relay did not reach Bob: " + JsonSerializer.Serialize(new {
                source = coordinator.ReadStatus("alice"),
                targetAttached = target is not null,
                targetPhase = target?.Engine.InspectExecutionBoundary().Phase.ToString(),
                targetTurnStatus = target?.GetCurrentTurn()?.Status,
                outboxes = stalled.InternalMailOutboxes.Select(row => new { row.State, row.QuarantineCode }),
                mails = stalled.Mails.Select(mail => new { mail.State, mail.RecoveryLastCode }),
                helperCalls = completion.HelperCalls
            }));
        }
        await transport.Started.Task.WaitAsync(Deadline);

        // Advance only the normal short polling cadence. This is below the
        // next heartbeat deadline, so the next A turn must come from a reply.
        clock.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => host.ReadMailboxStatus("alice").ReadyNoticeCount == 1
            || completion.CodexReplyReceived.Task.IsCompleted);
        if (!completion.CodexReplyReceived.Task.IsCompleted) clock.Advance(TimeSpan.FromSeconds(10));
        await completion.CodexReplyReceived.Task.WaitAsync(Deadline);
        await UntilAsync(() => host.ReadAttachedSession("alice")!.GetCurrentTurn() is null
            && host.ReadAttachedSession("bob")!.GetCurrentTurn() is null);

        CharacterSessionHost alice = host.ReadAttachedSession("alice")!;
        CharacterSessionHost bobHost = host.ReadAttachedSession("bob")!;
        SessionCompletedTurnProjection[] aliceTurns = alice.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns.Reverse().ToArray();
        Assert.Equal(2, aliceTurns.Length);
        Assert.Equal(["heartbeat-activation", "delegate-reply"], aliceTurns
            .Select(turn => turn.ObservationContent.JsonValue.GetProperty("kind").GetString()));
        Assert.All(aliceTurns, turn => Assert.Equal("runtime",
            turn.ObservationContent.JsonValue.GetProperty("sender").GetProperty("kind").GetString()));
        SessionCompletedTurnProjection bobTurn = Assert.Single(bobHost.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
        Assert.True(bobTurn.ObservationContent.IsStructured);
        Assert.Equal("inbound-mail", bobTurn.ObservationContent.JsonValue.GetProperty("kind").GetString());
        Assert.Equal("character", bobTurn.ObservationContent.JsonValue.GetProperty("sender").GetProperty("kind").GetString());
        Assert.Equal("alice", bobTurn.ObservationContent.JsonValue.GetProperty("sender").GetProperty("id").GetString());
        Assert.Equal("hello Bob", GalateaObservationContent.ReadMailboxContent(bobTurn.ObservationContent).Body);

        GalateaDelegationStateSnapshot durable = alice.DelegationHandle!.Store.ReadSnapshot();
        GalateaInternalMailOutboxSnapshot internalMail = Assert.Single(durable.InternalMailOutboxes);
        Assert.Equal(GalateaInternalMailState.Delivered, internalMail.State);
        Assert.Equal(bobTurn.ObservationContent, internalMail.BoundInput);
        GalateaReplyNoticeSnapshot reply = Assert.Single(durable.Notices);
        Assert.Equal(GalateaReplyNoticeState.Consumed, reply.State);
        Assert.Null(durable.ActiveLease);
        PlayerTurnNotice.Reply received = Assert.IsType<PlayerTurnNotice.Reply>(Assert.Single(
            GalateaObservationContent.ReadPlayerTurn(aliceTurns[1].ObservationContent).Notices));
        Assert.Equal(CodexFinal, received.Body);
        Assert.Equal("delegate", received.Sender!.Kind);
        Assert.Equal("codex", received.Sender.Id);
        Assert.Equal(1, transport.StartCalls);
        Assert.Equal(3, completion.MainInputs.Count);
        Assert.All(completion.MainInputs, input => Assert.NotEqual("player",
            input.GetProperty("sender").GetProperty("kind").GetString()));
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id, "openai-chat", "model-a", "openai-chat/strict", "http://localhost:8000/", ApiKey: "test-key");

    private static async Task UntilAsync(Func<bool> condition) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed class ScriptedCompletion : ICompletionClientFactory, ICompletionClient {
        internal TaskCompletionSource HeartbeatReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BobReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CodexReplyReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<JsonElement> MainInputs { get; } = new();
        private int _helperCalls;
        internal int HelperCalls => Volatile.Read(ref _helperCalls);
        public string Name => "zero-player-script";
        public string ApiSpecId => "openai-chat-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            ActionMessage response;
            if (request.PromptPrefix.OutputContract.Tools.Any(tool => tool.Name == OutboundMailExtractor.ToolName)) {
                Interlocked.Increment(ref _helperCalls);
                string input = Assert.IsType<string>(Assert.IsType<ObservationMessage>(Assert.Single(request.TailMessages)).Content);
                response = input.Contains(MailAction, StringComparison.Ordinal)
                    ? new ActionMessage([Mail("internal", "Bob", "hello Bob", "I sent Bob this message: hello Bob."),
                        Mail("external", "Codex", "inspect the garden", "I sent Codex this task: inspect the garden.")])
                    : new ActionMessage([]);
            }
            else {
                JsonElement input = MdJsonSerializer.Read(Assert.IsType<string>(request.PromptPrefix.SharedContextMessages.OfType<ObservationMessage>().Last().Content));
                MainInputs.Enqueue(input);
                string text = input.GetProperty("kind").GetString() switch {
                    "heartbeat-activation" => Observe(HeartbeatReceived, MailAction),
                    "inbound-mail" => Observe(BobReceived, "[Bob] I received the internal mail."),
                    "delegate-reply" => Observe(CodexReplyReceived, "[Galatea] I read the Codex result."),
                    _ => throw new InvalidDataException("No Player action is part of this scenario.")
                };
                observer?.OnTextDelta(text);
                response = new ActionMessage([new ActionBlock.Text(text)]);
            }
            return Task.FromResult(new CompletionResult(response, CompletionDescriptor.From(this, request)));
        }

        private static string Observe(TaskCompletionSource signal, string text) {
            signal.TrySetResult();
            return text;
        }

        private static ActionBlock.ToolCall Mail(string callId, string recipient, string body, string evidenceQuote) =>
            new(new RawToolCall(OutboundMailExtractor.ToolName, callId, JsonSerializer.Serialize(new {
                // The generated tool contract makes these string fields
                // optional; absence represents an unspecified subject/reply.
                recipient, body, evidenceQuote
            })));
    }

    private sealed class ScriptedTransport(Task bobReceived) : IGalateaDurableDelegateTransport {
        private GalateaStartDelegateTurnRequest? _started;
        private int _startCalls;
        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(GalateaEnsureDelegateBindingRequest request, CancellationToken ct) =>
            Task.FromResult(new GalateaDelegateBindingEstablished(request.BindingOperationId, "zero-player-thread"));

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(GalateaStartDelegateTurnRequest request, CancellationToken ct) {
            JsonElement task = MdJsonSerializer.Read(request.Task);
            Assert.Equal("inspect the garden", task.GetProperty("body").GetString());
            Assert.Equal("character", task.GetProperty("sender").GetProperty("kind").GetString());
            Assert.Equal("alice", task.GetProperty("sender").GetProperty("id").GetString());
            _started = request;
            Interlocked.Increment(ref _startCalls);
            Started.TrySetResult();
            return Task.FromResult(new GalateaDelegateTurnAccepted(request.DispatchId, request.ThreadId, "zero-player-turn"));
        }

        public async Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(GalateaInspectDelegateDispatchRequest request, CancellationToken ct) {
            await bobReceived.WaitAsync(ct);
            GalateaStartDelegateTurnRequest started = Assert.IsType<GalateaStartDelegateTurnRequest>(_started);
            Assert.Equal(started.DispatchId, request.DispatchId);
            Assert.Equal(GalateaTaskCommitment.FromTask(started.Task), request.TaskCommitment);
            return new GalateaDelegateDispatchInspection.Completed(request.DispatchId, request.ThreadId,
                "zero-player-turn", CodexFinal, GalateaDelegateInspectionSource.Live);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
