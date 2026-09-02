using System.Security.Cryptography;
using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteDefaultPodRecallTests {
    private static readonly DateTimeOffset Timestamp = new(
        2026,
        9,
        2,
        17,
        0,
        0,
        TimeSpan.FromHours(8)
    );
    private static readonly CompletionDescriptor Invocation = new(
        "recall-tests",
        "test-v1",
        "main-model"
    );

    [Theory]
    [InlineData(null, GalateaMemoRecallMvpPolicy.DefaultMaxTokens)]
    [InlineData(73, 73)]
    public async Task ProviderUsesNamedOptionsAndReturnsNoMatch(
        int? configuredMaxTokens,
        int expectedMaxTokens
    ) {
        using var fixture = await RuntimeFixture.CreateAsync();
        var client = new RecallCompletionClient(_ => []);
        var provider = new GalateaDefaultMemoPodRecallProvider(
            fixture.Reconciler,
            Connection(configuredMaxTokens),
            () => client
        );

        IReadOnlyList<PlayerTurnRecall> recalls =
            await provider.SelectRecallsAsync(
                Request(RecallBarrier.Empty,
                    CharacterNoteOriginBarrier.Empty),
                CancellationToken.None
            );

        Assert.Empty(recalls);
        CompletionRequest request = Assert.Single(client.Requests);
        Assert.Equal("memo-recall-model", request.ModelId);
        Assert.Equal(expectedMaxTokens, request.MaxTokens);
        Assert.Contains(
            GalateaMemoRecallQueryRenderer.SchemaId,
            Assert.IsType<ObservationMessage>(
                Assert.Single(request.TailMessages)
            ).Content,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MemoPodLimits.MaximumRecallMaxTokens + 1)]
    public async Task ProviderRejectsInvalidConnectionMaxTokens(int value) {
        using var fixture = await RuntimeFixture.CreateAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GalateaDefaultMemoPodRecallProvider(
                fixture.Reconciler,
                Connection(value),
                () => new RecallCompletionClient(_ => [])
            ));
    }

    [Fact]
    public async Task ProviderDoesNotConvertInvalidMemoPodOutputToNoMatch() {
        using var fixture = await RuntimeFixture.CreateAsync();
        var client = new RecallCompletionClient(_ => ["m1:00000001"]);
        var provider = new GalateaDefaultMemoPodRecallProvider(
            fixture.Reconciler,
            Connection(maxTokens: null),
            () => client
        );

        MemoRecallException failure = await Assert.ThrowsAsync<
            MemoRecallException>(() => provider.SelectRecallsAsync(
                Request(RecallBarrier.Empty,
                    CharacterNoteOriginBarrier.Empty),
                CancellationToken.None
            ).AsTask());

        Assert.Equal(
            MemoRecallFailureKind.InvalidModelOutput,
            failure.FailureKind
        );
    }

    [Fact]
    public async Task RecallReleasesPodGateAndKeepsOpenedFrozenEpoch() {
        using var fixture = await RuntimeFixture.CreateAsync();
        var client = new BlockingRecallCompletionClient();

        Task<MemoRecallResult> recall = fixture.Reconciler
            .RecallSettledDefaultPodAsync(
                client,
                "memo-recall-model",
                "query",
                Options(),
                CancellationToken.None
            );
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        CharacterNotePendingReconcileResult pending =
            await fixture.Reconciler.ReconcilePendingAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<CharacterNotePendingReconcileResult.NoPending>(pending);

        global::Atelia.MemoPod.MemoPod advanced =
            global::Atelia.MemoPod.MemoPod.Open(
                fixture.StatePath,
                CharacterNoteDefaultPodV1.PodId
            );
        advanced.ResumeEditing();
        _ = advanced.Append("advanced after recall snapshot");
        await advanced.FreezeAsync();

        client.Release();
        Assert.Empty((await recall).Memos);
    }

    [Fact]
    public async Task RecallRejectsSettledAuthorityMismatchBeforeProvider() {
        using var fixture = await RuntimeFixture.CreateAsync();
        global::Atelia.MemoPod.MemoPod advanced =
            global::Atelia.MemoPod.MemoPod.Open(
                fixture.StatePath,
                CharacterNoteDefaultPodV1.PodId
            );
        advanced.ResumeEditing();
        _ = advanced.Append("unauthorized advance");
        await advanced.FreezeAsync();
        var client = new RecallCompletionClient(_ => []);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Reconciler.RecallSettledDefaultPodAsync(
                client,
                "memo-recall-model",
                "query",
                Options(),
                CancellationToken.None
            ));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task RecallRejectsActiveExactTextCapture() {
        using var fixture = await RuntimeFixture.CreateAsync();
        EventAddress source = fixture.AppendAction("save this");
        _ = fixture.Store.CaptureNew(new CharacterMemoryCaptureRequest(
            EventAddressTextCodec.Format(source),
            Sha256("save this"),
            Encoding.UTF8.GetByteCount("save this"),
            "recall-test-extractor-v1",
            ["memo"]
        ));
        var client = new RecallCompletionClient(_ => []);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Reconciler.RecallSettledDefaultPodAsync(
                client,
                "memo-recall-model",
                "query",
                Options(),
                CancellationToken.None
            ));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task RecallRecoversActivePlannedDerivedInfoBeforeDispatch() {
        var access = new FaultBeforeNextFreezePodAccess();
        using var fixture = await RuntimeFixture.CreateAsync(
            access,
            new OneNoteExtractor()
        );
        EventAddress source = fixture.AppendAction("remember the blue door");
        CharacterNoteAppliedMemo applied = Assert.Single(Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.AppliedNow
        >(await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            new GalateaTerminalActionExtractionTarget(
                source,
                "remember the blue door"
            )
        )).Memos);
        access.FaultNextFreeze();
        CharacterNoteDerivedInfoReconcileResult interrupted =
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                new FixedDerivedInfoEnricher()
            );
        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Deferred>(
            interrupted
        );
        Assert.NotNull(fixture.Reconciler.ReadStatusSnapshot()
            .ActiveDerivedInfoWork);
        var client = new RecallCompletionClient(
            _ => [applied.MemoId.Value]
        );
        var provider = new GalateaDefaultMemoPodRecallProvider(
            fixture.Reconciler,
            Connection(maxTokens: null),
            () => client
        );

        PlayerTurnRecall recall = Assert.Single(
            await provider.SelectRecallsAsync(
                Request(RecallBarrier.Empty,
                    CharacterNoteOriginBarrier.Empty),
                CancellationToken.None
            )
        );

        Assert.Null(fixture.Reconciler.ReadStatusSnapshot()
            .ActiveDerivedInfoWork);
        Assert.Equal(
            "标题：Blue door\n\n正文：\nremember the blue door",
            recall.Body
        );
    }

    [Fact]
    public async Task ProviderPropagatesCallerCancellation() {
        using var fixture = await RuntimeFixture.CreateAsync();
        var client = new BlockingRecallCompletionClient();
        var provider = new GalateaDefaultMemoPodRecallProvider(
            fixture.Reconciler,
            Connection(maxTokens: null),
            () => client
        );
        using var cancellation = new CancellationTokenSource();

        Task<IReadOnlyList<PlayerTurnRecall>> selecting = provider
            .SelectRecallsAsync(
                Request(RecallBarrier.Empty,
                    CharacterNoteOriginBarrier.Empty),
                cancellation.Token
            )
            .AsTask();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => selecting
        );
    }

    [Fact]
    public async Task PlannerSkipsMissingTitleAndBothBarriersInOrder() {
        using var pod = await PlannerPod.CreateAsync(
            ("no title", null),
            ("origin blocked", "Origin"),
            ("recall blocked", "Prior"),
            ("selected body", "Selected")
        );
        Memo[] memos = pod.Pod.List().ToArray();
        Memo originBlocked = memos[1];
        Memo recallBlocked = memos[2];
        Memo selected = memos[3];
        var origin = new CharacterNoteVisibleActionIdentity(
            new EventAddress(
                Atelia.Data.SizedPtr.Create(12, 4),
                1,
                AddressHint.None
            ),
            GalateaVisibleActionFingerprint.Derive("visible action")
        );
        var originBarrier = new CharacterNoteOriginBarrier([
            new CharacterNoteOriginBarrierEntry(
                CharacterNoteDefaultPodV1.PodId,
                originBlocked.Id,
                origin
            )
        ]);
        var recallBarrier = new RecallBarrier([
            new RecallEntry(
                RecallType.MemoExactText,
                GalateaMemoRecallSourceIdCodec.Format(
                    CharacterNoteDefaultPodV1.PodId,
                    recallBlocked.Id
                )
            )
        ]);

        PlayerTurnRecall recall = Assert.Single(
            GalateaDefaultMemoPodRecallPlanner.Select(
                Request(recallBarrier, originBarrier),
                CharacterNoteDefaultPodV1.PodId,
                memos
            )
        );

        Assert.Equal(
            GalateaMemoRecallSourceIdCodec.Format(
                CharacterNoteDefaultPodV1.PodId,
                selected.Id
            ),
            recall.Entry.SourceId
        );
        Assert.Equal(RecallType.MemoExactText,
            recall.Entry.RecallType);
        Assert.Equal(
            "标题：Selected\n\n正文：\nselected body",
            recall.Body
        );
    }

    [Fact]
    public async Task PlannerTreatsRecallOnlyAggregateOverflowAsUnderfill() {
        string exactText = new(
            'x',
            MemoPodLimits.MaximumMemoExactTextUtf8Bytes
        );
        string title = new(
            't',
            MemoPodLimits.MaximumMemoTitleUtf8Bytes
        );
        using var pod = await PlannerPod.CreateAsync(
            (exactText, title),
            ("small enough", "Fallback")
        );
        var crowded = new PlayerTurnObservation(
            new string('p', GalateaHttpV1.MaximumMessageUtf8Bytes),
            Timestamp,
            [
                new PlayerTurnNotice.Reply(new string('a', 256 * 1024)),
                new PlayerTurnNotice.Reply(new string('b', 256 * 1024)),
                new PlayerTurnNotice.Reply(new string('c', 256 * 1024))
            ]
        );
        _ = PlayerTurnObservationEnvelope.Wrap(crowded);

        PlayerTurnRecall recall = Assert.Single(
            GalateaDefaultMemoPodRecallPlanner.Select(
                Request(
                    RecallBarrier.Empty,
                    CharacterNoteOriginBarrier.Empty,
                    crowded
                ),
                CharacterNoteDefaultPodV1.PodId,
                pod.Pod.List()
            )
        );

        Assert.Equal(
            GalateaMemoRecallSourceIdCodec.Format(
                CharacterNoteDefaultPodV1.PodId,
                pod.Pod.List()[1].Id
            ),
            recall.Entry.SourceId
        );
    }

    private static MemoRecallOptions Options() => new(
        GalateaMemoRecallMvpPolicy.MaxResults,
        GalateaMemoRecallMvpPolicy.DefaultMaxTokens,
        GalateaMemoRecallMvpPolicy.MaximumFrozenPromptUtf8Bytes,
        GalateaMemoRecallMvpPolicy.MaximumHydratedExactTextUtf8Bytes
    );

    private static CompletionConnectionConfig Connection(int? maxTokens) =>
        new(
            "memo-recall",
            "test",
            "memo-recall-model",
            "surface",
            "https://example.invalid",
            MaxTokens: maxTokens
        );

    private static GalateaPlayerTurnRecallRequest Request(
        RecallBarrier recallBarrier,
        CharacterNoteOriginBarrier originBarrier,
        PlayerTurnObservation? observation = null
    ) => new(
        new GalateaUserConfig(
            "alice",
            "password",
            new GalateaCharacterName("Galatea"),
            new GalateaPlayerName("Player"),
            "/session",
            "/delegation",
            "/memory",
            GalateaSessionProvisioning.ExistingOnly,
            "system"
        ),
        new EventAddress(
            Atelia.Data.SizedPtr.Create(4, 4),
            1,
            AddressHint.None
        ),
        observation ?? new PlayerTurnObservation("act", Timestamp),
        new GalateaPlayerTurnRecallContext(
            recallBarrier,
            originBarrier
        )
    );

    private static string Sha256(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))
    );

    private sealed class NullExtractor : ICharacterNoteExtractor {
        public string ContractId => "recall-null-extractor-v1";

        public ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
            string visibleActionText,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<CharacterNoteIntent>>(
                []
            );
        }
    }

    private sealed class OneNoteExtractor : ICharacterNoteExtractor {
        public string ContractId => "recall-one-note-extractor-v1";

        public ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
            string visibleActionText,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<CharacterNoteIntent>>([
                new CharacterNoteIntent(
                    visibleActionText,
                    visibleActionText
                )
            ]);
        }
    }

    private sealed class FixedDerivedInfoEnricher
        : ICharacterNoteDerivedInfoEnricher {
        public string ContractId => "recall-derived-info-v1";

        public ValueTask<IReadOnlyList<CharacterNoteDerivedInfo>> EnrichAsync(
            CharacterNoteDerivedInfoEnrichmentRequest request,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<
                CharacterNoteDerivedInfo>>(request.Targets.Select(
                    static target => new CharacterNoteDerivedInfo(
                        target.ArtifactOrdinal,
                        "Blue door",
                        "A remembered blue door.",
                        "The character wants to remember the blue door."
                    )
                ).ToArray());
        }
    }

    private sealed class RuntimeFixture : IDisposable {
        private readonly FixturePaths _paths;
        private readonly SessionJournalEngine _engine;

        private RuntimeFixture(
            FixturePaths paths,
            SessionJournalEngine engine,
            CharacterMemorySqliteStore store,
            CharacterNoteDefaultPodReconciler reconciler
        ) {
            _paths = paths;
            _engine = engine;
            Store = store;
            Reconciler = reconciler;
        }

        internal string StatePath => _paths.StatePath;
        internal SessionJournalEngine Engine => _engine;
        internal CharacterMemorySqliteStore Store { get; }
        internal CharacterNoteDefaultPodReconciler Reconciler { get; }

        internal static async ValueTask<RuntimeFixture> CreateAsync(
            ICharacterNoteDefaultPodAccess? access = null,
            ICharacterNoteExtractor? extractor = null
        ) {
            var paths = new FixturePaths();
            SessionJournalEngine engine = SessionJournalEngine.Create(
                paths.SessionPath,
                new SessionCreateOptions(
                    "main-model",
                    "system",
                    "surface"
                )
            );
            CharacterMemorySqliteStore store =
                CharacterMemorySqliteStore.CreateNew(
                    paths.StatePath,
                    new CharacterMemoryStoreOwner("alice", engine.Path),
                    new CharacterMemoryStoreBaseline(
                        engine.ReadView.ReadPhysicalAppendFrontier(),
                        EventAddressTextCodec.FormatNullable(
                            engine.ReadCurrentHead()
                        )
                    ),
                    CharacterNoteDefaultPodV1.EmptyStateIdentity
                );
            CharacterNoteDefaultPodReconciler reconciler =
                await CharacterNoteDefaultPodReconciler.AttachAsync(
                    store,
                    extractor ?? new NullExtractor(),
                    access
                );
            return new RuntimeFixture(paths, engine, store, reconciler);
        }

        internal ValueTask<CharacterNoteDerivedInfoEnrichmentRequest>
            Materialize(
            CharacterMemoryDerivedInfoWorkSnapshot work,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(
            CharacterNoteDerivedInfoContextMaterializer.Materialize(
                _engine,
                work,
                cancellationToken
            )
        );

        internal EventAddress AppendAction(string visibleText) {
            _ = _engine.AppendObservation("observation");
            return _engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text(visibleText)]),
                Invocation
            );
        }

        public void Dispose() {
            Reconciler.Dispose();
            _engine.Dispose();
            _paths.Dispose();
        }
    }

    private sealed class FaultBeforeNextFreezePodAccess
        : ICharacterNoteDefaultPodAccess {
        private int _faultNextFreeze;

        internal void FaultNextFreeze() =>
            Interlocked.Exchange(ref _faultNextFreeze, 1);

        public ICharacterNoteDefaultPodHandle Create(
            string rootPath,
            MemoPodId podId,
            string topic
        ) => new Handle(
            this,
            CharacterNoteMemoPodAccess.Instance.Create(
                rootPath,
                podId,
                topic
            )
        );

        public ICharacterNoteDefaultPodHandle Open(
            string rootPath,
            MemoPodId podId
        ) => new Handle(
            this,
            CharacterNoteMemoPodAccess.Instance.Open(rootPath, podId)
        );

        private bool ConsumeFreezeFault() =>
            Interlocked.Exchange(ref _faultNextFreeze, 0) != 0;

        private sealed class Handle(
            FaultBeforeNextFreezePodAccess owner,
            ICharacterNoteDefaultPodHandle inner
        ) : ICharacterNoteDefaultPodHandle {
            public MemoPodId PodId => inner.PodId;
            public MemoPodPhase Phase => inner.Phase;
            public int ActiveMemoCount => inner.ActiveMemoCount;
            public int ActiveExactTextUtf8Bytes =>
                inner.ActiveExactTextUtf8Bytes;
            public int ActiveDerivedInfoUtf8Bytes =>
                inner.ActiveDerivedInfoUtf8Bytes;

            public MemoId Append(string exactText) =>
                inner.Append(exactText);

            public Memo Get(MemoId id) => inner.Get(id);

            public void UpdateDerivedInfo(
                MemoId id,
                string title,
                string gist,
                string summary
            ) => inner.UpdateDerivedInfo(id, title, gist, summary);

            public void ResumeEditing() => inner.ResumeEditing();

            public string ComputeStateIdentity() =>
                inner.ComputeStateIdentity();

            public Task FreezeAsync(
                CancellationToken cancellationToken = default
            ) {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.ConsumeFreezeFault()) {
                    throw new CharacterNoteDefaultPodAccessException(
                        CharacterNoteDefaultPodFailureKind.IoFailure,
                        "simulated failure before Default Pod publish"
                    );
                }
                return inner.FreezeAsync(cancellationToken);
            }

            public void ConfirmCurrentDocumentDurability() =>
                inner.ConfirmCurrentDocumentDurability();

            public Task<MemoRecallResult> RecallAsync(
                ICompletionClient completionClient,
                string modelId,
                string query,
                MemoRecallOptions options,
                CancellationToken cancellationToken = default
            ) => inner.RecallAsync(
                completionClient,
                modelId,
                query,
                options,
                cancellationToken
            );
        }
    }

    private class RecallCompletionClient(
        Func<CompletionRequest, string[]> select
    ) : ICompletionClient {
        public string Name => "memo-recall-test";
        public string ApiSpecId => "test-v1";
        internal List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => CompleteAsync(request, cancellationToken);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            Assert.Equal(
                PromptCacheReuseHint.ReuseExpectedSoon,
                invocationOptions.PromptCacheReuseHint
            );
            return CompleteAsync(request, cancellationToken);
        }

        protected virtual Task<CompletionResult> CompleteAsync(
            CompletionRequest request,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            string arguments = "{\"memoIds\":["
                + string.Join(",", select(request).Select(
                    static id => $"\"{id}\""
                ))
                + "]}";
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.ToolCall(
                    new RawToolCall(
                        "recall_memos",
                        "call_recall",
                        arguments
                    )
                )]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed class BlockingRecallCompletionClient
        : RecallCompletionClient {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal BlockingRecallCompletionClient() : base(_ => []) { }

        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal void Release() => _release.TrySetResult();

        protected override async Task<CompletionResult> CompleteAsync(
            CompletionRequest request,
            CancellationToken cancellationToken
        ) {
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await base.CompleteAsync(request, cancellationToken);
        }
    }

    private sealed class PlannerPod : IDisposable {
        private readonly string _root;

        private PlannerPod(
            string root,
            global::Atelia.MemoPod.MemoPod pod
        ) {
            _root = root;
            Pod = pod;
        }

        internal global::Atelia.MemoPod.MemoPod Pod { get; }

        internal static async ValueTask<PlannerPod> CreateAsync(
            params (string ExactText, string? Title)[] items
        ) {
            string root = Path.Combine(
                Path.GetTempPath(),
                "atelia-galatea-recall-planner-tests-"
                    + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(root);
            global::Atelia.MemoPod.MemoPod pod =
                global::Atelia.MemoPod.MemoPod.Create(
                    root,
                    CharacterNoteDefaultPodV1.PodId,
                    "planner tests"
                );
            foreach ((string exactText, string? title) in items) {
                _ = pod.Append(exactText, title);
            }
            await pod.FreezeAsync();
            return new PlannerPod(root, pod);
        }

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
    }

    private sealed class FixturePaths : IDisposable {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "atelia-default-pod-recall-tests-"
                + Guid.NewGuid().ToString("N")
        );

        internal string SessionPath => Path.Combine(_root, "session");
        internal string StatePath => Path.Combine(_root, "memory");

        internal FixturePaths() => Directory.CreateDirectory(_root);

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
    }
}
