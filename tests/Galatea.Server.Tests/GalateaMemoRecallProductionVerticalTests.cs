using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.Galatea.Prompts;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaMemoRecallProductionVerticalTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EnabledBindingRunsSelectorBeforeMainOnNoMatch() {
        var main = new MainCompletionClient();
        var recall = new RecallCompletionClient();
        await using var host = CreateHost(main, recall);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.IsType<GalateaDefaultMemoPodRecallProvider>(
            session.PlayerTurnRecallProvider
        );

        GalateaLiveTurn turn = service.StartTurn(
            session,
            "寻找和旧城区有关的记忆",
            new GalateaTurnOptions("test")
        );
        await service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            )
            .WaitAsync(Deadline);
        service.FinishTurn(session, turn);

        CompletionRequest recallRequest = Assert.Single(recall.Requests);
        Assert.Empty(recallRequest.PromptPrefix.SharedContextMessages
            .OfType<ActionMessage>());
        string query = Assert.IsType<string>(
            Assert.IsType<ObservationMessage>(
                Assert.Single(recallRequest.TailMessages)
            ).Content
        );
        Assert.Contains(
            GalateaMemoRecallQueryRenderer.SchemaId,
            query,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "寻找和旧城区有关的记忆",
            query,
            StringComparison.Ordinal
        );
        Assert.Single(main.Requests);

        PlayerTurnObservation observation = Assert.Single(
            session.Engine.ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns
        ).ObservationContent is string stored
            && PlayerTurnObservationEnvelope.TryUnwrap(
                stored,
                out PlayerTurnObservation parsed
            )
                ? parsed
                : throw new Xunit.Sdk.XunitException(
                    "The production turn did not persist a canonical Observation."
                );
        Assert.Empty(observation.Recalls);
    }

    [Theory]
    [InlineData("player-action")]
    [InlineData("heartbeat-activation")]
    [InlineData("delegate-reply")]
    public async Task SelectedMemoIsHydratedInjectedAndPersistedWithTypedTriggerAndBarriers(
        string triggerKind
    ) {
        const string exactText = "旧城区的蓝门后藏着一把钥匙。";
        const string title = "旧城区的蓝门";
        var main = new MainCompletionClient([
            "[Galatea] 我把“旧城区的蓝门后藏着一把钥匙。”作为长期Note提交给runtime保存。",
            "origin already visible",
            "main reply after recall",
            "recall already visible",
        ]);
        var recall = new RecallCompletionClient {
            EmitNoteOnFirstExtraction = true,
        };
        recall.EnqueueSelection();
        recall.EnqueueSelection("m1:00000001");
        recall.EnqueueSelection("m1:00000001");
        recall.EnqueueSelection("m1:00000001");
        await using var host = CreateHost(main, recall);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        _ = await RunTypedTurnAsync(service, session, triggerKind,
            "请把刚才的发现记下来");

        await WaitUntilAsync(() => {
            try {
                global::Atelia.MemoPod.MemoPod pod =
                    global::Atelia.MemoPod.MemoPod.Open(
                        host.CharacterMemoryStateDirectory,
                        CharacterNoteDefaultPodV1.PodId
                    );
                Memo memo = pod.Get(MemoId.Parse("m1:00000001"));
                return memo.Title == title && memo.ExactText == exactText;
            }
            catch (IOException) {
                return false;
            }
        });

        // Selector may nominate the Memo, but the selected source Action
        // remains visible: automatic triggers must preserve the origin barrier.
        _ = await RunTypedTurnAsync(service, session, triggerKind,
            "那扇蓝门后有什么？");
        Assert.Empty(ReadPersistedObservation(session).Recalls);

        for (int index = 0; index < 2; index++) {
            EventAddress head = session.Engine.ReadCurrentHead()!.Value;
            // Test-only branch movement through the journal API: the dev UI
            // intentionally offers rewind only for ordinary Player turns.
            Assert.IsType<SessionTurnRetractionResult.Moved>(
                session.Engine.RewindLatestCompletedTurn(head));
        }

        _ = await RunTypedTurnAsync(service, session, triggerKind,
            "那扇蓝门后有什么？");

        Assert.Equal(3, recall.Requests.Count);
        Assert.Equal(3, main.Requests.Count);
        ObservationMessage finalMessage = main.Requests[2]
            .PromptPrefix.SharedContextMessages
            .OfType<ObservationMessage>()
            .Last();
        string finalContent = Assert.IsType<string>(finalMessage.Content);
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            finalContent,
            out PlayerTurnObservation requestedObservation
        ));
        PlayerTurnRecall selected = Assert.Single(
            requestedObservation.Recalls
        );
        Assert.Equal(RecallType.MemoExactText,
            selected.Entry.RecallType);
        Assert.Equal(
            GalateaMemoRecallSourceIdCodec.Format(
                CharacterNoteDefaultPodV1.PodId,
                MemoId.Parse("m1:00000001")
            ),
            selected.Entry.SourceId
        );
        Assert.Equal(
            $"标题：{title}\n\n正文：\n{exactText}",
            selected.Body
        );

        SessionCompletedTurnProjection persisted = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.Equal(finalContent, persisted.ObservationContent);

        _ = await RunTypedTurnAsync(service, session, triggerKind,
            "继续使用刚才的记忆");
        Assert.Empty(ReadPersistedObservation(session).Recalls);
        Assert.Equal(4, recall.Requests.Count);
        Assert.Equal(4, main.Requests.Count);
        for (int index = 0; index < 4; index++) {
            string observationContent = Assert.IsType<string>(main.Requests[index]
                .PromptPrefix.SharedContextMessages.OfType<ObservationMessage>()
                .Last().Content);
            Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
                observationContent, out PlayerTurnObservation observation));
            using JsonDocument selectorEnvelope = JsonDocument.Parse(Assert.IsType<string>(
                Assert.IsType<ObservationMessage>(Assert.Single(
                    recall.Requests[index].TailMessages)).Content));
            Assert.Equal("atelia.memo-pod.recall-query.v1",
                selectorEnvelope.RootElement.GetProperty("schema").GetString());
            using JsonDocument query = JsonDocument.Parse(Assert.IsType<string>(
                selectorEnvelope.RootElement.GetProperty("query").GetString()));
            Assert.Equal("atelia.galatea.memo-recall-context.v2",
                query.RootElement.GetProperty("schema").GetString());
            JsonElement current = query.RootElement.GetProperty("currentTurn");
            Assert.Equal(triggerKind,
                current.GetProperty("trigger").GetProperty("kind").GetString());
            Assert.Equal(observation.ExternalLocalTimestamp,
                current.GetProperty("externalLocalTimestamp").GetDateTimeOffset());
            Assert.Equal(triggerKind switch {
                "player-action" => PlayerTurnObservationTriggerKind.PlayerAction,
                "heartbeat-activation" => PlayerTurnObservationTriggerKind.HeartbeatActivation,
                _ => PlayerTurnObservationTriggerKind.DelegateReply,
            }, observation.TriggerKind);
            if (triggerKind == "delegate-reply") {
                Assert.Equal("外层执行者已恢复，请继续查看蓝门。",
                    Assert.Single(current.GetProperty("externalNotices")
                        .EnumerateArray()).GetProperty("text").GetString());
                Assert.False(current.GetProperty("trigger")
                    .TryGetProperty("playerText", out _));
            }
        }
    }

    private static async Task<GalateaLiveTurn> RunTypedTurnAsync(
        GalateaHostService service,
        UserSessionHost session,
        string triggerKind,
        string playerText
    ) {
        GalateaLiveTurn turn;
        if (triggerKind == "delegate-reply") {
            await SeedReadyReplyAsync(session.DelegationHandle!);
            turn = Assert.IsType<GalateaReadyReplyTurnStartResult.Started>(
                service.StartReadyReplyTurn(session, new GalateaTurnOptions("test"))).Turn;
            Assert.NotNull(turn.DurableReplyLease);
        }
        else {
            turn = triggerKind == "player-action"
                ? service.StartTurn(session, playerText, new GalateaTurnOptions("test"))
                : session.StartTurn(new GalateaFreshInput.HeartbeatActivation(
                    new GalateaCharacterName("Alice")), new GalateaTurnOptions("test"));
        }
        try {
            await service.RunTurnAsync(session, turn, CancellationToken.None)
                .WaitAsync(Deadline);
            Assert.Equal("completed", turn.Status);
        }
        finally {
            // Direct typed heartbeat admission has no cadence claim.
            session.FinishTurn(turn);
        }
        return turn;
    }

    private static async Task SeedReadyReplyAsync(GalateaDelegationSessionHandle handle) {
        GalateaDelegationSqliteStore store = handle.Store;
        int ordinal = store.ReadSnapshot().Captures.Count + 1;
        // Each fixture dispatch has its own valid, non-colliding source identity.
        // Outbound extraction is disabled here; only ready-reply admission is tested.
        string source = "ej1:" + (0x1000 + ordinal).ToString("x16")
            + "0000000100000000";
        GalateaDelegationCaptureResult captured = store.CaptureActionBatch(new(
            source, new string('a', 64), VisibleActionUtf8Bytes: 6,
            "extractor-contract-v1", [new SendMailIntent(
                GalateaDelegateConfigReader.CanonicalRecipient,
                Subject: null, Body: "seed task", InReplyToMessageId: null,
                EvidenceQuote: "seeded")]));
        string dispatchId = Assert.Single(captured.DispatchIds);
        // The production supervisor is the sole writer of binding/dispatch
        // transitions. Directly seeding those states raced its fallback pulse.
        _ = handle.Signal();
        await WaitUntilAsync(() => store.ReadSnapshot().Notices.Any(notice =>
            notice.DispatchId == dispatchId && notice.State == GalateaReplyNoticeState.Ready));
    }

    private static PlayerTurnObservation ReadPersistedObservation(UserSessionHost session) {
        string content = Assert.Single(session.Engine.ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns).ObservationContent;
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(content,
            out PlayerTurnObservation observation));
        return observation;
    }

    [Fact]
    public async Task ConfiguredSelectorFailurePreventsMainCompletion() {
        var main = new MainCompletionClient();
        var recall = new RecallCompletionClient {
            Failure = new IOException("selector unavailable")
        };
        await using var host = CreateHost(main, recall);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "继续",
            new GalateaTurnOptions("test")
        );

        await Assert.ThrowsAsync<MemoRecallException>(() =>
            service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            )
        );
        Assert.Single(recall.Requests);
        Assert.Empty(main.Requests);
    }

    [Fact]
    public async Task MaintenanceModeLeavesConfiguredRecallDisabled() {
        var main = new MainCompletionClient();
        var recall = new RecallCompletionClient();
        await using var host = CreateHost(
            main,
            recall,
            maintenanceMode: true
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        Assert.IsType<DisabledGalateaPlayerTurnRecallProvider>(
            session.PlayerTurnRecallProvider
        );
        Assert.Empty(recall.Requests);
        Assert.Empty(main.Requests);
    }

    private static GalateaTestHost CreateHost(
        MainCompletionClient main,
        RecallCompletionClient recall,
        bool maintenanceMode = false
    ) {
        main.RecallDispatchCount = () => recall.Requests.Count;
        var factory = new RoutingClientFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            ["test"] = main,
            ["recall"] = recall,
        });
        return GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: maintenanceMode,
            connections: [
                Connection("test", "main-model"),
                Connection("recall", "recall-model"),
            ],
            selectableConnectionIds: ["test"],
            characterNoteExtractorConnectionId: "recall",
            memoRecallConnectionId: "recall",
            delegateTransport: new CompletedDelegateTransport()
        );
    }

    private static async Task WaitUntilAsync(Func<bool> condition) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition()) {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static CompletionConnectionConfig Connection(
        string id,
        string modelId
    ) => new(
        id,
        "openai-chat",
        modelId,
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private sealed class RoutingClientFactory(
        IReadOnlyDictionary<string, ICompletionClient> clients
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => clients[connection.Id];
    }

    /// <summary>Controlled external boundary; the real driver owns all durable transitions.</summary>
    private sealed class CompletedDelegateTransport : IGalateaDurableDelegateTransport {
        private const string ThreadId = "seed-thread";
        private readonly ConcurrentDictionary<string, GalateaStartDelegateTurnRequest> _started =
            new(StringComparer.Ordinal);
        private string? _bindingOperationId;

        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            string? previous = Interlocked.CompareExchange(ref _bindingOperationId, request.BindingOperationId, null);
            Assert.True(previous is null || previous == request.BindingOperationId,
                "The controlled delegate must not silently establish a different binding.");
            return Task.FromResult(new GalateaDelegateBindingEstablished(request.BindingOperationId, ThreadId));
        }

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            Assert.NotNull(Volatile.Read(ref _bindingOperationId));
            Assert.Equal(ThreadId, request.ThreadId);
            Assert.True(_started.TryAdd(request.DispatchId, request),
                "The controlled delegate must not accept a duplicate dispatch.");
            return Task.FromResult(new GalateaDelegateTurnAccepted(
                request.DispatchId, ThreadId, TurnId(request.DispatchId)));
        }

        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            Assert.True(_started.TryGetValue(request.DispatchId, out var started),
                "The controlled delegate cannot complete an unknown dispatch.");
            Assert.Equal(started!.ThreadId, request.ThreadId);
            Assert.Equal(started.Task, request.Task);
            Assert.Equal(TurnId(request.DispatchId), request.ExpectedTurnId);
            return Task.FromResult<GalateaDelegateDispatchInspection>(new GalateaDelegateDispatchInspection.Completed(
                request.DispatchId, ThreadId, TurnId(request.DispatchId),
                "外层执行者已恢复，请继续查看蓝门。", GalateaDelegateInspectionSource.Live));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static string TurnId(string dispatchId) => "seed-turn-" + dispatchId;
    }

    private sealed class MainCompletionClient : ICompletionClient {
        private readonly Queue<string> _replies;

        internal MainCompletionClient(IEnumerable<string>? replies = null) {
            _replies = new Queue<string>(replies ?? ["main reply"]);
        }

        public string Name => "memo-recall-main-test";
        public string ApiSpecId => "test-v1";
        internal List<CompletionRequest> Requests { get; } = [];
        internal Func<int>? RecallDispatchCount { get; set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(
                RecallDispatchCount?.Invoke() > Requests.Count,
                "Memo recall selector must complete before each main dispatch."
            );
            Requests.Add(request);
            string reply = _replies.Dequeue();
            observer?.OnTextDelta(reply);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(reply)]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed class RecallCompletionClient : ICompletionClient {
        public string Name => "memo-recall-selector-test";
        public string ApiSpecId => "test-v1";
        internal List<CompletionRequest> Requests { get; } = [];
        internal Exception? Failure { get; init; }
        internal bool EmitNoteOnFirstExtraction { get; init; }
        private readonly Queue<string[]> _selections = new();
        private int _noteExtractionCount;

        internal void EnqueueSelection(params string[] memoIds) =>
            _selections.Enqueue(memoIds);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            bool derivedInfo = request.PromptPrefix.OutputContract.Tools.Any(
                static tool => string.Equals(
                    tool.Name,
                    CharacterNoteDerivedInfoEnricher.ToolName,
                    StringComparison.Ordinal
                )
            );
            ActionMessage message;
            if (derivedInfo) {
                message = new ActionMessage([new ActionBlock.ToolCall(
                    new RawToolCall(
                        CharacterNoteDerivedInfoEnricher.ToolName,
                        "call-derived-info",
                        JsonSerializer.Serialize(new {
                            items = new[] { new {
                                artifactOrdinal = 0,
                                title = "旧城区的蓝门",
                                gist = "蓝门后藏着一把钥匙。",
                                summary = "旧城区的蓝门后藏着一把钥匙。",
                            } },
                        })
                    )
                )]);
            }
            else {
                int call = Interlocked.Increment(
                    ref _noteExtractionCount
                );
                message = EmitNoteOnFirstExtraction && call == 1
                    ? new ActionMessage([new ActionBlock.ToolCall(
                        new RawToolCall(
                            CharacterNoteExtractor.ToolName,
                            "call-note",
                            JsonSerializer.Serialize(new {
                                exactText = "旧城区的蓝门后藏着一把钥匙。",
                                evidenceQuote = "[Galatea] 我把“旧城区的蓝门后藏着一把钥匙。”作为长期Note提交给runtime保存。",
                            })
                        )
                    )])
                    : new ActionMessage([]);
            }
            return Task.FromResult(new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            ));
        }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Failure is not null) { throw Failure; }
            string[] selected = _selections.Count == 0
                ? []
                : _selections.Dequeue();
            string arguments = JsonSerializer.Serialize(new {
                memoIds = selected,
            });
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.ToolCall(
                    new RawToolCall(
                        "recall_memos",
                        "call-recall",
                        arguments
                    )
                )]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }
}
