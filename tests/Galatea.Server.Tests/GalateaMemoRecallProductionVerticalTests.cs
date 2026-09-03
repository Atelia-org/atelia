using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task SelectedMemoIsHydratedInjectedAndPersisted() {
        const string exactText = "旧城区的蓝门后藏着一把钥匙。";
        const string title = "旧城区的蓝门";
        var main = new MainCompletionClient([
            "[Galatea] 我把“旧城区的蓝门后藏着一把钥匙。”作为长期Note提交给runtime保存。",
            "main reply after recall",
        ]);
        var recall = new RecallCompletionClient {
            EmitNoteOnFirstExtraction = true,
        };
        recall.EnqueueSelection();
        recall.EnqueueSelection("m1:00000001");
        await using var host = CreateHost(main, recall);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        GalateaLiveTurn first = service.StartTurn(
            session,
            "请把刚才的发现记下来",
            new GalateaTurnOptions("test")
        );
        await service.RunTurnAsync(
                session,
                first,
                CancellationToken.None
            )
            .WaitAsync(Deadline);
        service.FinishTurn(session, first);

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

        EventAddress firstHead = session.Engine.ReadCurrentHead()!.Value;
        Assert.NotNull(service.PrepareAndCommitPopLatestTurn(
            session,
            firstHead
        ));

        GalateaLiveTurn second = service.StartTurn(
            session,
            "那扇蓝门后有什么？",
            new GalateaTurnOptions("test")
        );
        await service.RunTurnAsync(
                session,
                second,
                CancellationToken.None
            )
            .WaitAsync(Deadline);
        service.FinishTurn(session, second);

        Assert.Equal(2, recall.Requests.Count);
        Assert.Equal(2, main.Requests.Count);
        ObservationMessage finalMessage = main.Requests[1]
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
            memoRecallConnectionId: "recall"
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
