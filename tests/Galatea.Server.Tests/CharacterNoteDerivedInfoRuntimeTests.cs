using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteDerivedInfoRuntimeTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static readonly CompletionDescriptor Invocation = new(
        "derived-info-runtime-test",
        "derived-info-runtime-test-v1",
        "model-a"
    );
    private const string VisibleAction =
        "[Galatea] I write a note: remember blue.";
    private const string ExactText = "remember blue";

    [Fact]
    public async Task BlockedProviderDoesNotDelayTurnOrReceiptAndRunsOutsideTurnLock() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig helper = Connection("helper");
        var helperClient = new RoleAwareHelperClient(
            DerivedInfoBehavior.Block
        );
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(VisibleAction)
                )),
                [helper.Id] = helperClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, helper],
            selectableConnectionIds: [main.Id],
            characterNoteExtractorConnectionId: helper.Id
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.NotNull(session.CharacterNoteDerivedInfoPump);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn;
        try {
            turn = service.StartTurn(
                session,
                "first",
                new GalateaTurnOptions(main.Id)
            );
            await service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, session.NoteSaveReceipts.Count);
            Assert.False(helperClient.DerivedInfoStarted.Task.IsCompleted);
        }
        finally {
            session.TurnLock.Release();
        }

        await helperClient.DerivedInfoStarted.Task.WaitAsync(Deadline);
        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();
        helperClient.ReleaseDerivedInfo();

        await WaitUntilAsync(() => {
            Memo memo = OpenMemo(session);
            return memo.Title == "Blue title"
                && memo.Gist == "Blue gist."
                && memo.Summary == "Blue summary";
        });
        Assert.Null(session.CharacterMemoryReconciler!
            .ReadStatusSnapshot().ActiveDerivedInfoWork);
    }

    [Fact]
    public async Task ProviderTimeoutLeavesPendingAndNextTurnCanTriggerRetry() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig helper = Connection("helper");
        var helperClient = new RoleAwareHelperClient(
            DerivedInfoBehavior.TimeoutThenBlock
        );
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(
                    Message(new ActionBlock.Text(VisibleAction)),
                    Message(new ActionBlock.Text("[Galatea] I continue."))
                ),
                [helper.Id] = helperClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, helper],
            selectableConnectionIds: [main.Id],
            characterNoteExtractorConnectionId: helper.Id
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        service.CharacterNoteDerivedInfoDeadlineForTest =
            TimeSpan.FromMilliseconds(75);
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        await RunTurnUnderLockAsync(service, session, main.Id, "first");
        await helperClient.FirstDerivedInfoCanceled.Task.WaitAsync(Deadline);
        Assert.Equal(1, helperClient.DerivedInfoDispatchCount);
        Assert.Null(OpenMemo(session).Title);

        await session.TurnLock.WaitAsync();
        try {
            await service.ReconcileDurableAdmissionAsync(
                session,
                CancellationToken.None
            );
            GalateaLiveTurn second = service.StartTurn(
                session,
                "second",
                new GalateaTurnOptions(main.Id)
            );
            await service.RunTurnAsync(
                    session,
                    second,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, second);
            Assert.Equal("completed", second.Status);
            Assert.Equal(1, helperClient.DerivedInfoDispatchCount);
        }
        finally {
            session.TurnLock.Release();
        }

        await helperClient.SecondDerivedInfoStarted.Task.WaitAsync(Deadline);
        Assert.Equal(2, helperClient.DerivedInfoDispatchCount);
    }

    [Fact]
    public async Task OneSignalProcessesAtMostOneWorkAndLaterSignalAdvancesQueue() {
        await using var fixture = await PumpFixture.CreateAsync();
        fixture.AppendAndCapture("first observation", "first note");
        fixture.AppendAndCapture("second observation", "second note");

        Assert.True(fixture.Pump!.Signal());
        await fixture.Enricher.WaitForCallsAsync(1);
        await WaitUntilAsync(() => fixture.AppliedMemoCount() == 1);
        await Task.Delay(100);
        Assert.Equal(1, fixture.Enricher.CallCount);
        Assert.Equal(1, fixture.AppliedMemoCount());

        Assert.True(fixture.Pump!.Signal());
        await fixture.Enricher.WaitForCallsAsync(2);
        await WaitUntilAsync(() => fixture.AppliedMemoCount() == 2);
    }

    [Fact]
    public async Task PumpDisposeCancelsAndAwaitsBlockedProvider() {
        await using var fixture = await PumpFixture.CreateAsync();
        fixture.Enricher.BlockUntilCanceled = true;
        fixture.AppendAndCapture("observation", "blocked note");
        Assert.True(fixture.Pump!.Signal());
        await fixture.Enricher.WaitForCallsAsync(1);

        await fixture.Pump.DisposeAsync().AsTask().WaitAsync(Deadline);

        Assert.True(fixture.Enricher.CancellationObserved);
        Assert.False(fixture.Pump.Signal());
        Assert.Null(fixture.ReadMemos().Single().Title);
    }

    [Theory]
    [InlineData(ActivePlanMode.Valid, null)]
    [InlineData(ActivePlanMode.InvalidTarget,
        "character-memory-quarantined")]
    [InlineData(ActivePlanMode.Unavailable,
        "character-memory-settlement-deferred")]
    public async Task ActivePlanAdmissionRecoversWithoutCallingProvider(
        ActivePlanMode mode,
        string? expectedReason
    ) {
        await using var fixture = await PumpFixture.CreateAsync(
            createPump: false
        );
        fixture.AppendAndCapture("observation", "planned note");
        fixture.PrepareAndPlan(mode);
        if (mode is ActivePlanMode.Unavailable) {
            fixture.PodAccess.OpenUnavailable = true;
        }
        await using UserSessionHost host = fixture.CreateHost();

        if (expectedReason is null) {
            await GalateaHostService
                .ReconcileActiveCharacterNoteDerivedInfoPlanAsync(host);
            Assert.Null(fixture.Reconciler.ReadStatusSnapshot()
                .ActiveDerivedInfoWork);
            Assert.Equal("Title 0", fixture.ReadMemos().Single().Title);
        }
        else {
            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => GalateaHostService
                    .ReconcileActiveCharacterNoteDerivedInfoPlanAsync(host)
                    .AsTask());
            Assert.Equal(expectedReason, failure.FailureReason);
        }
        Assert.Equal(0, fixture.Enricher.CallCount);
    }

    [Fact]
    public async Task DisabledAndMaintenanceSessionsDoNotCreatePump() {
        CompletionConnectionConfig main = Connection("test");
        var factory = new RoutingFactory(new Dictionary<string,
            ICompletionClient>(StringComparer.Ordinal) {
            [main.Id] = new QueueClient(),
        });
        await using (GalateaTestHost disabled = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main],
            selectableConnectionIds: [main.Id]
        )) {
            GalateaHostService service = disabled.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost session = await service.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            Assert.Null(session.CharacterMemoryReconciler);
            Assert.Null(session.CharacterNoteDerivedInfoPump);
        }

        await using GalateaTestHost maintenance = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: true,
            connections: [main],
            selectableConnectionIds: [main.Id],
            characterNoteExtractorConnectionId: main.Id
        );
        GalateaHostService maintenanceService = maintenance.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost maintenanceSession = await maintenanceService
            .GetSessionAsync("alice", CancellationToken.None);
        Assert.Null(maintenanceSession.CharacterMemoryReconciler);
        Assert.Null(maintenanceSession.CharacterNoteDerivedInfoPump);
    }

    [Fact]
    public void ProductionFactoryCreatesPerUserLazyEnrichersFromNoteBinding() {
        CompletionConnectionConfig connection = Connection("helper");
        IReadOnlyDictionary<string, GalateaUserConfig> users = new[] {
            User("first"),
            User("second"),
        }.ToDictionary(static user => user.UserId, StringComparer.Ordinal);
        int clientRequests = 0;

        IReadOnlyDictionary<string, ICharacterNoteDerivedInfoEnricher>
            enrichers = GalateaHostService
                .CreateCharacterNoteDerivedInfoEnrichers(
                    users,
                    connection,
                    () => {
                        Interlocked.Increment(ref clientRequests);
                        return new QueueClient();
                    }
                );

        Assert.Equal(2, enrichers.Count);
        Assert.NotSame(enrichers["first"], enrichers["second"]);
        Assert.Equal(0, clientRequests);
        Assert.Empty(GalateaHostService
            .CreateCharacterNoteDerivedInfoEnrichers(
                users,
                connection: null,
                () => throw new Xunit.Sdk.XunitException(
                    "Disabled DerivedInfo composition requested a client."
                )
            ));
    }

    private static async Task RunTurnUnderLockAsync(
        GalateaHostService service,
        UserSessionHost session,
        string connectionId,
        string playerText
    ) {
        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                playerText,
                new GalateaTurnOptions(connectionId)
            );
            await service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, turn);
            Assert.Equal("completed", turn.Status);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    private static Memo OpenMemo(UserSessionHost session) =>
        global::Atelia.MemoPod.MemoPod.Open(
            session.User.CharacterMemoryStateDir,
            CharacterNoteDefaultPodV1.PodId
        ).List().Single();

    private static CompletionConnectionConfig Connection(string id) => new(
        id,
        "openai-chat",
        "model-a",
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static GalateaUserConfig User(string id) => new(
        id,
        "password",
        new GalateaCharacterName("Galatea " + id),
        new GalateaPlayerName("Player " + id),
        "/session/" + id,
        "/delegation/" + id,
        "/memory/" + id,
        GalateaSessionProvisioning.ExistingOnly,
        "system"
    );

    private static ActionMessage Message(params ActionBlock[] blocks) =>
        new(blocks);

    private static ActionBlock.ToolCall NoteTool() => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        "note-call",
        JsonSerializer.Serialize(new {
            exactText = ExactText,
            evidenceQuote = "I write a note: remember blue",
        })
    ));

    private static ActionBlock.ToolCall DerivedInfoTool() => new(
        new RawToolCall(
            CharacterNoteDerivedInfoEnricher.ToolName,
            "derived-info-call",
            JsonSerializer.Serialize(new {
                items = new[] { new {
                    artifactOrdinal = 0,
                    title = "Blue title",
                    gist = "Blue gist.",
                    summary = "Blue summary",
                } },
            })
        )
    );

    private static async Task WaitUntilAsync(Func<bool> condition) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition()) {
            await Task.Delay(10, deadline.Token);
        }
    }

    public enum DerivedInfoBehavior {
        Block,
        TimeoutThenBlock,
    }

    public enum ActivePlanMode {
        Valid,
        InvalidTarget,
        Unavailable,
    }

    private sealed class RoleAwareHelperClient(
        DerivedInfoBehavior behavior
    ) : ICompletionClient {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _noteDispatchCount;
        private int _derivedInfoDispatchCount;

        public string Name => "role-aware-helper";
        public string ApiSpecId => "test-v1";
        internal int DerivedInfoDispatchCount =>
            Volatile.Read(ref _derivedInfoDispatchCount);
        internal TaskCompletionSource DerivedInfoStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal TaskCompletionSource FirstDerivedInfoCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondDerivedInfoStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseDerivedInfo() => _release.TrySetResult();

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            bool derivedInfo = request.PromptPrefix.OutputContract.Tools.Any(
                static tool => string.Equals(
                    tool.Name,
                    CharacterNoteDerivedInfoEnricher.ToolName,
                    StringComparison.Ordinal
                )
            );
            ActionMessage message;
            if (!derivedInfo) {
                int noteCall = Interlocked.Increment(
                    ref _noteDispatchCount
                );
                message = noteCall == 1
                    ? Message(NoteTool())
                    : Message();
            }
            else {
                int call = Interlocked.Increment(
                    ref _derivedInfoDispatchCount
                );
                DerivedInfoStarted.TrySetResult();
                if (call == 2) {
                    SecondDerivedInfoStarted.TrySetResult();
                }
                try {
                    await _release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (
                    behavior is DerivedInfoBehavior.TimeoutThenBlock
                    && call == 1) {
                    FirstDerivedInfoCanceled.TrySetResult();
                    throw;
                }
                message = Message(DerivedInfoTool());
            }
            return new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class QueueClient(params ActionMessage[] messages)
        : ICompletionClient {
        private readonly Queue<ActionMessage> _messages = new(messages);
        private readonly object _gate = new();

        public string Name => "derived-info-runtime-queue";
        public string ApiSpecId => "test-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            ActionMessage message;
            lock (_gate) {
                message = _messages.Dequeue();
            }
            foreach (ActionBlock.Text text in message.Blocks
                         .OfType<ActionBlock.Text>()) {
                observer?.OnTextDelta(text.Content);
            }
            return Task.FromResult(new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed class RoutingFactory(
        IReadOnlyDictionary<string, ICompletionClient> clients
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => clients[connection.Id];
    }

    private sealed class RecordingEnricher : ICharacterNoteDerivedInfoEnricher {
        private readonly SemaphoreSlim _calls = new(0);
        private int _cancellationObserved;
        private int _callCount;

        public string ContractId => "derived-info-runtime-enricher-v1";
        internal int CallCount => Volatile.Read(ref _callCount);
        internal bool BlockUntilCanceled { get; set; }
        internal bool CancellationObserved =>
            Volatile.Read(ref _cancellationObserved) != 0;

        public async ValueTask<IReadOnlyList<CharacterNoteDerivedInfo>> EnrichAsync(
            CharacterNoteDerivedInfoEnrichmentRequest request,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            _calls.Release();
            if (BlockUntilCanceled) {
                try {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken
                    );
                }
                catch (OperationCanceledException) {
                    Volatile.Write(ref _cancellationObserved, 1);
                    throw;
                }
            }
            return request.Targets.Select(static target =>
                new CharacterNoteDerivedInfo(
                    target.ArtifactOrdinal,
                    $"Title {target.ArtifactOrdinal}",
                    $"Gist {target.ArtifactOrdinal}.",
                    $"Summary {target.ArtifactOrdinal}"
                )
            ).ToArray();
        }

        internal async Task WaitForCallsAsync(int count) {
            while (CallCount < count) {
                await _calls.WaitAsync().WaitAsync(Deadline);
            }
        }
    }

    private sealed class RecordingNoteExtractor : ICharacterNoteExtractor {
        public string ContractId => "derived-info-runtime-note-v1";

        public ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
            string visibleActionText,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<CharacterNoteIntent>>([
                new CharacterNoteIntent(
                    "Exact memo: " + visibleActionText,
                    visibleActionText
                ),
            ]);
        }
    }

    private sealed class PumpFixture : IAsyncDisposable {
        private readonly string _root;
        private readonly CharacterMemorySqliteStore _store;
        private readonly SemaphoreSlim? _pumpTurnLock;

        private PumpFixture(
            string root,
            SessionJournalEngine engine,
            CharacterMemorySqliteStore store,
            CharacterNoteDefaultPodReconciler reconciler,
            RecordingEnricher enricher,
            TogglePodAccess podAccess,
            CharacterNoteDerivedInfoPump? pump,
            SemaphoreSlim? pumpTurnLock
        ) {
            _root = root;
            Engine = engine;
            _store = store;
            Reconciler = reconciler;
            Enricher = enricher;
            PodAccess = podAccess;
            Pump = pump;
            _pumpTurnLock = pumpTurnLock;
        }

        internal SessionJournalEngine Engine { get; }
        internal CharacterNoteDefaultPodReconciler Reconciler { get; }
        internal RecordingEnricher Enricher { get; }
        internal TogglePodAccess PodAccess { get; }
        internal CharacterNoteDerivedInfoPump? Pump { get; }

        internal static async ValueTask<PumpFixture> CreateAsync(
            bool createPump = true
        ) {
            string root = Path.Combine(
                Path.GetTempPath(),
                "atelia-derived-info-runtime-tests-"
                    + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(root);
            string sessionPath = Path.Combine(root, "session");
            string statePath = Path.Combine(root, "memory");
            SessionJournalEngine engine = SessionJournalEngine.Create(
                sessionPath,
                new SessionCreateOptions("model-a", "system-a", "surface-a")
            );
            var extractor = new RecordingNoteExtractor();
            var podAccess = new TogglePodAccess();
            CharacterMemorySqliteStore store =
                CharacterMemorySqliteStore.CreateNew(
                    statePath,
                    new CharacterMemoryStoreOwner("user", engine.Path),
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
                    extractor,
                    podAccess
                );
            var enricher = new RecordingEnricher();
            SemaphoreSlim? pumpTurnLock = createPump
                ? new SemaphoreSlim(1, 1)
                : null;
            CharacterNoteDerivedInfoPump? pump = createPump
                ? new CharacterNoteDerivedInfoPump(
                    reconciler,
                    enricher,
                    engine,
                    pumpTurnLock!
                )
                : null;
            return new PumpFixture(
                root,
                engine,
                store,
                reconciler,
                enricher,
                podAccess,
                pump,
                pumpTurnLock
            );
        }

        internal void AppendAndCapture(
            string observation,
            string visibleAction
        ) {
            _ = Engine.AppendObservation(observation);
            EventAddress source = Engine.AppendImportedAgentAction(
                Message(new ActionBlock.Text(visibleAction)),
                Invocation
            );
            CharacterNoteDefaultPodReconcileResult result = Reconciler
                .ReconcileTargetAsync(
                    Engine,
                    new GalateaTerminalActionExtractionTarget(
                        source,
                        visibleAction
                    )
                )
                .AsTask().GetAwaiter().GetResult();
            Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
                result
            );
        }

        internal int AppliedMemoCount() => ReadMemos().Count(static memo =>
            memo.Title is not null
        );

        internal IReadOnlyList<Memo> ReadMemos() =>
            global::Atelia.MemoPod.MemoPod.Open(
                Path.Combine(_root, "memory"),
                CharacterNoteDefaultPodV1.PodId
            ).List();

        internal void PrepareAndPlan(ActivePlanMode mode) {
            CharacterMemoryDerivedInfoWorkSnapshot work = _store
                .ReadNextDerivedInfoWork(pendingAfter: null)
                ?? throw new Xunit.Sdk.XunitException(
                    "Expected pending DerivedInfo work."
                );
            CharacterMemoryDerivedInfoValue[] values = work.Notes.Select(
                static note => new CharacterMemoryDerivedInfoValue(
                    note.ArtifactOrdinal,
                    $"Title {note.ArtifactOrdinal}",
                    $"Gist {note.ArtifactOrdinal}.",
                    $"Summary {note.ArtifactOrdinal}"
                )
            ).ToArray();
            work = _store.PrepareDerivedInfo(new(
                work.SourceActionAddress,
                work.ExtractionCommitment,
                Enricher.ContractId,
                values
            )).Work;
            string baseIdentity = Reconciler.ReadStatusSnapshot()
                .SettledDefaultPodStateIdentity!;
            string targetIdentity;
            if (mode is ActivePlanMode.InvalidTarget) {
                targetIdentity = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes("invalid-target"))
                ).ToLowerInvariant();
            }
            else {
                global::Atelia.MemoPod.MemoPod candidate =
                    global::Atelia.MemoPod.MemoPod.Open(
                        Path.Combine(_root, "memory"),
                        CharacterNoteDefaultPodV1.PodId
                    );
                candidate.ResumeEditing();
                foreach (CharacterMemoryDerivedInfoNoteSnapshot note
                         in work.Notes) {
                    candidate.UpdateDerivedInfo(
                        MemoId.Parse(note.MemoId),
                        note.Title!,
                        note.Gist!,
                        note.Summary!
                    );
                }
                targetIdentity = candidate.ComputeStateIdentity();
            }
            _ = _store.PlanDerivedInfo(new(
                work.SourceActionAddress,
                work.ExtractionCommitment,
                work.DerivedInfoCommitment!,
                baseIdentity,
                targetIdentity
            ));
        }

        internal UserSessionHost CreateHost() {
            var user = new GalateaUserConfig(
                "user",
                "password",
                new GalateaCharacterName("Galatea"),
                new GalateaPlayerName("Player"),
                Engine.Path,
                Path.Combine(_root, "delegation"),
                Path.Combine(_root, "memory"),
                GalateaSessionProvisioning.ExistingOnly,
                "system"
            );
            return new UserSessionHost(
                user,
                Engine,
                new RecentTurnsResponseDto([], null,
                    ContextHeaderDto.Empty),
                GalateaRecapGridTargetExpectation.ForNames(
                    user.CharacterName,
                    user.PlayerName
                ),
                Reconciler,
                delegationHandle: null,
                DisabledOutboundMailExtractor.Instance,
                new RecordingNoteExtractor(),
                derivedInfoEnricher: null,
                derivedInfoProviderDeadline: null
            );
        }

        public async ValueTask DisposeAsync() {
            if (Pump is not null) {
                await Pump.DisposeAsync();
            }
            Reconciler.Dispose();
            Engine.Dispose();
            _pumpTurnLock?.Dispose();
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
        }
    }

    private sealed class TogglePodAccess : ICharacterNoteDefaultPodAccess {
        internal bool OpenUnavailable { get; set; }

        public ICharacterNoteDefaultPodHandle Create(
            string rootPath,
            MemoPodId podId,
            string topic
        ) => CharacterNoteMemoPodAccess.Instance.Create(
            rootPath,
            podId,
            topic
        );

        public ICharacterNoteDefaultPodHandle Open(
            string rootPath,
            MemoPodId podId
        ) {
            if (OpenUnavailable) {
                throw new CharacterNoteDefaultPodAccessException(
                    CharacterNoteDefaultPodFailureKind.IoFailure,
                    "simulated runtime open failure"
                );
            }
            return CharacterNoteMemoPodAccess.Instance.Open(
                rootPath,
                podId
            );
        }
    }
}
