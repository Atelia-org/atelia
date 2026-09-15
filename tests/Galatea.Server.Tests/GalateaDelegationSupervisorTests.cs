using System.Collections.Concurrent;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationSupervisorTests {
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SharedTransportUsesEachHomeAndReopensOriginalThreadsAfterHomeChange() {
        using var root = new OwnedRoot();
        GalateaCharacterConfig[] users = [
            User("alice", root.Child("sessions/alice"), root.Child("delegation/alice")),
            User("bob", root.Child("sessions/bob"), root.Child("delegation/bob"))
        ];
        using SessionJournalEngine alice = CreateSession(users[0].SessionDir);
        using SessionJournalEngine bob = CreateSession(users[1].SessionDir);
        GalateaConfig config = Config(root.Path, users);
        for (int i = 0; i < users.Length; i++) {
            using var store = CreateStore(i == 0 ? alice : bob, users[i], config.Delegates);
            CaptureHomeMail(store, 1);
        }

        var first = new HomeTransport();
        await using (var supervisor = new GalateaDelegationSupervisor(
            config, first,
            testHooks: new(PulseInterval: TimeSpan.FromMilliseconds(10)))) {
            await WaitUntilAsync(() => users.All(user =>
                supervisor.ReadMailboxStatus(user.CharacterId).ReadyNoticeCount == 1));
        }
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, first.Bindings.Count);
        Assert.Equal(2, first.Starts.Count);
        Assert.Equal(users.Select(user => user.HomeDir).Order(),
            first.Starts.Select(request => request.Cwd).Order());

        GalateaCharacterConfig[] moved = users.Select(user => user with {
            HomeDir = Directory.CreateDirectory(user.HomeDir + "-moved").FullName
        }).ToArray();
        GalateaConfig movedConfig = config with { Characters = moved };
        foreach (GalateaCharacterConfig user in moved) {
            using var store = GalateaDelegationSqliteStore.OpenExisting(
                user.DelegationStateDir, Owner(user, config.Delegates),
                Limits(config.Delegates.CodexRoute));
            CaptureHomeMail(store, 2);
        }
        var second = new HomeTransport();
        await using (var supervisor = new GalateaDelegationSupervisor(
            movedConfig, second,
            testHooks: new(PulseInterval: TimeSpan.FromMilliseconds(10)))) {
            await WaitUntilAsync(() => moved.All(user =>
                supervisor.ReadMailboxStatus(user.CharacterId).ReadyNoticeCount == 2));
        }
        Assert.Equal(1, second.DisposeCount);
        Assert.Empty(second.Bindings);
        Assert.Equal(2, second.Starts.Count);
        foreach (GalateaCharacterConfig user in users) {
            string thread = Assert.Single(first.Starts, request => request.Cwd == user.HomeDir).ThreadId;
            Assert.Equal(thread, Assert.Single(second.Starts,
                request => request.Cwd == user.HomeDir + "-moved").ThreadId);
        }
    }

    private static void CaptureHomeMail(GalateaDelegationSqliteStore store, int sequence) =>
        store.CaptureActionBatch(new GalateaDelegationCaptureRequest(
            $"ej1:{sequence:x16}0000000100000000", new string('a', 64),
            VisibleActionUtf8Bytes: 12, "extractor-contract-v1",
            [new SendMailIntent("Codex", null, "write personal.txt", null, "sent")], GalateaDelegationTestInputs.Sender(store, "Galatea")));

    private sealed class HomeTransport : IGalateaDurableDelegateTransport {
        internal ConcurrentQueue<GalateaEnsureDelegateBindingRequest> Bindings { get; } = new();
        internal ConcurrentQueue<GalateaStartDelegateTurnRequest> Starts { get; } = new();
        internal int DisposeCount { get; private set; }

        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request, CancellationToken ct) {
            Bindings.Enqueue(request);
            return Task.FromResult(new GalateaDelegateBindingEstablished(
                request.BindingOperationId, "thread-" + request.BindingOperationId));
        }

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request, CancellationToken ct) {
            Starts.Enqueue(request);
            return Task.FromResult(new GalateaDelegateTurnAccepted(
                request.DispatchId, request.ThreadId, "turn-" + request.DispatchId));
        }

        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request, CancellationToken ct) =>
            Task.FromResult<GalateaDelegateDispatchInspection>(new GalateaDelegateDispatchInspection.Completed(
                request.DispatchId, request.ThreadId, "turn-" + request.DispatchId,
                "written", GalateaDelegateInspectionSource.Persistent));

        public ValueTask DisposeAsync() {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task LazyAttachCreatesExactBaselineAndHandleIsBorrowed() {
        using var root = new OwnedRoot();
        string sessionPath = root.Child("sessions/alice");
        string statePath = root.Child("delegation/alice");
        using SessionJournalEngine engine = CreateSession(sessionPath);
        var transport = new ProbeTransport();
        await using var supervisor = new GalateaDelegationSupervisor(
            Config(root.Path, [User("alice", sessionPath, statePath)]),
            transport,
            testHooks: NoWorkHooks(TimeSpan.FromDays(1))
        );

        Assert.Equal(
            GalateaDelegationCharacterAvailability.Uninitialized,
            supervisor.ReadStatus("alice").Availability
        );
        Assert.False(Path.Exists(statePath));
        var expectedFrontier = engine.ReadView.ReadPhysicalAppendFrontier();
        string? expectedHead = engine.ReadView.ReadCurrentHead() is { } head
            ? EventAddressTextCodec.Format(head)
            : null;

        GalateaDelegationSessionHandle handle =
            supervisor.AttachWritableSession("alice", engine);
        GalateaDelegationSqliteStore borrowed = handle.Store;
        GalateaDelegationStateSnapshot snapshot = borrowed.ReadSnapshot();

        Assert.Equal(expectedFrontier,
            snapshot.Baseline.CaptureFromPhysicalFrontier);
        Assert.Equal(expectedHead, snapshot.Baseline.SelectedHead);
        Assert.Equal(
            GalateaDelegationSupervisor.CreateSessionRepositoryId(
                sessionPath
            ),
            snapshot.Owner.SessionRepositoryId
        );
        Assert.Equal(
            GalateaDelegationCharacterAvailability.Writable,
            supervisor.ReadStatus("alice").Availability
        );

        Parallel.For(0, 100, _ => handle.Dispose());
        Assert.Equal(snapshot.Owner, borrowed.ReadSnapshot().Owner);
        Assert.Throws<ObjectDisposedException>(() => _ = handle.Store);
        engine.Dispose();
        using (SessionJournalEngine retry =
               SessionJournalEngine.Open(sessionPath)) {
            using GalateaDelegationSessionHandle retryHandle =
                supervisor.AttachWritableSession("alice", retry);
            Assert.Equal(snapshot.Owner,
                retryHandle.Store.ReadSnapshot().Owner);
        }
        Assert.Equal(0, transport.ExternalCallCount);
    }

    [Fact]
    public async Task ExistingStoreEagerlyReopensAndMaintenanceIsZeroCall() {
        using var root = new OwnedRoot();
        string sessionPath = root.Child("sessions/alice");
        string statePath = root.Child("delegation/alice");
        using SessionJournalEngine engine = CreateSession(sessionPath);
        GalateaConfig writableConfig = Config(
            root.Path,
            [User("alice", sessionPath, statePath)]
        );
        using (GalateaDelegationSqliteStore created = CreateStore(
                   engine,
                   writableConfig.Characters[0],
                   writableConfig.Delegates)) {
            _ = created.CaptureActionBatch(
                new GalateaDelegationCaptureRequest(
                    "ej1:00000000000000010000000100000000",
                    new string('a', 64),
                    VisibleActionUtf8Bytes: 12,
                    "extractor-contract-v1",
                    [new SendMailIntent(
                        "Codex",
                        Subject: "secret subject",
                        Body: "secret body",
                        InReplyToMessageId: null,
                        EvidenceQuote: "sent"
                    )]
                , GalateaDelegationTestInputs.Sender(created, "Galatea"))
            );
        }

        var writableTransport = new ProbeTransport();
        await using (var supervisor = new GalateaDelegationSupervisor(
                         writableConfig,
                         writableTransport,
                         testHooks: NoWorkHooks(TimeSpan.FromDays(1)))) {
            Assert.Equal(
                GalateaDelegationCharacterAvailability.Writable,
                supervisor.ReadStatus("alice").Availability
            );
            Assert.ThrowsAny<IOException>(() =>
                GalateaDelegationSqliteStore.OpenExisting(
                    statePath,
                    Owner(writableConfig.Characters[0],
                        writableConfig.Delegates),
                    Limits(writableConfig.Delegates.CodexRoute)
                ));
        }
        Assert.Equal(1, writableTransport.DisposeCount);

        GalateaConfig maintenanceConfig = writableConfig with {
            MaintenanceMode = true
        };
        var maintenanceTransport = new ProbeTransport();
        int pulseCount = 0;
        await using (var supervisor = new GalateaDelegationSupervisor(
                         maintenanceConfig,
                         maintenanceTransport,
                         testHooks: new(
                             TimeSpan.FromMilliseconds(10),
                             (_, _, _) => {
                                 Interlocked.Increment(ref pulseCount);
                                 return Task.FromResult(NoWork());
                             }
                         ))) {
            Assert.True(supervisor.IsMaintenanceMode);
            Assert.Equal(
                GalateaDelegationCharacterAvailability.ReadOnly,
                supervisor.ReadStatus("alice").Availability
            );
            Assert.False(supervisor.Signal());
            await Task.Delay(50);
            Assert.Equal(0, Volatile.Read(ref pulseCount));
            GalateaMailboxStatusProjection mailboxStatus =
                supervisor.ReadMailboxStatus("alice");
            Assert.Equal(
                GalateaMailboxStatusState.Unavailable,
                mailboxStatus.State
            );
            Assert.Equal("MAINTENANCE_READ_ONLY", mailboxStatus.Code);
            Assert.Equal(1, mailboxStatus.QueuedCount);
            Assert.Equal(0, mailboxStatus.ReadyNoticeCount);
            Assert.Equal(0, mailboxStatus.AttemptCount);
            Assert.Null(mailboxStatus.NextRetryAtUnixTimeMilliseconds);
            GalateaDelegationCharacterUnavailableException failure =
                Assert.Throws<GalateaDelegationCharacterUnavailableException>(
                    () => supervisor.AttachWritableSession("alice", engine)
                );
            Assert.Equal("MAINTENANCE_READ_ONLY", failure.Code);
            Assert.Equal(0, maintenanceTransport.ExternalCallCount);
        }
        Assert.Equal(1, maintenanceTransport.DisposeCount);
    }

    [Fact]
    public async Task MergedSignalsCoalesceAndCompletionResignalsOnce() {
        using var root = new OwnedRoot();
        string sessionPath = root.Child("sessions/alice");
        string statePath = root.Child("delegation/alice");
        using SessionJournalEngine engine = CreateSession(sessionPath);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        int active = 0;
        int maximumActive = 0;
        var hooks = new GalateaDelegationSupervisorTestHooks(
            TimeSpan.FromDays(1),
            async (_, _, _) => {
                int call = Interlocked.Increment(ref calls);
                int current = Interlocked.Increment(ref active);
                SetMaximum(ref maximumActive, current);
                try {
                    if (call == 1) {
                        entered.TrySetResult();
                        await release.Task.ConfigureAwait(false);
                    }
                    return NoWork();
                }
                finally {
                    Interlocked.Decrement(ref active);
                }
            }
        );
        await using var supervisor = new GalateaDelegationSupervisor(
            Config(root.Path, [User("alice", sessionPath, statePath)]),
            new ProbeTransport(),
            testHooks: hooks
        );
        using GalateaDelegationSessionHandle handle =
            supervisor.AttachWritableSession("alice", engine);
        await entered.Task.WaitAsync(TestDeadline);

        for (int index = 0; index < 100; index++) {
            Assert.True(supervisor.Signal());
        }
        await Task.Delay(25);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.TrySetResult();

        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
        await Task.Delay(50);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, Volatile.Read(ref maximumActive));
    }

    [Fact]
    public async Task PeriodicFallbackPulsesWithoutANewSignal() {
        using var root = new OwnedRoot();
        string sessionPath = root.Child("sessions/alice");
        string statePath = root.Child("delegation/alice");
        using SessionJournalEngine engine = CreateSession(sessionPath);
        int calls = 0;
        var hooks = new GalateaDelegationSupervisorTestHooks(
            TimeSpan.FromMilliseconds(20),
            (_, _, _) => {
                Interlocked.Increment(ref calls);
                return Task.FromResult(NoWork());
            }
        );
        await using var supervisor = new GalateaDelegationSupervisor(
            Config(root.Path, [User("alice", sessionPath, statePath)]),
            new ProbeTransport(),
            testHooks: hooks
        );
        using GalateaDelegationSessionHandle handle =
            supervisor.AttachWritableSession("alice", engine);

        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 1);
        int afterAttachSignal = Volatile.Read(ref calls);
        await WaitUntilAsync(() =>
            Volatile.Read(ref calls) > afterAttachSignal);

        Assert.True(Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task DifferentUsersPulseConcurrentlyButEachUserDoesNot() {
        using var root = new OwnedRoot();
        string aliceSession = root.Child("sessions/alice");
        string bobSession = root.Child("sessions/bob");
        using SessionJournalEngine alice = CreateSession(aliceSession);
        using SessionJournalEngine bob = CreateSession(bobSession);
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var perUserActive = new ConcurrentDictionary<string, int>(
            StringComparer.Ordinal
        );
        var perUserMaximum = new ConcurrentDictionary<string, int>(
            StringComparer.Ordinal
        );
        int globalActive = 0;
        int globalMaximum = 0;
        int firstEntries = 0;
        var hooks = new GalateaDelegationSupervisorTestHooks(
            TimeSpan.FromDays(1),
            async (userId, _, _) => {
                int userActive = perUserActive.AddOrUpdate(
                    userId,
                    1,
                    static (_, value) => value + 1
                );
                perUserMaximum.AddOrUpdate(
                    userId,
                    userActive,
                    (_, value) => Math.Max(value, userActive)
                );
                int currentGlobal = Interlocked.Increment(ref globalActive);
                SetMaximum(ref globalMaximum, currentGlobal);
                try {
                    if (Interlocked.Increment(ref firstEntries) == 2) {
                        bothEntered.TrySetResult();
                    }
                    await release.Task.ConfigureAwait(false);
                    return NoWork();
                }
                finally {
                    perUserActive.AddOrUpdate(
                        userId,
                        0,
                        static (_, value) => value - 1
                    );
                    Interlocked.Decrement(ref globalActive);
                }
            }
        );
        await using var supervisor = new GalateaDelegationSupervisor(
            Config(root.Path, [
                User("alice", aliceSession, root.Child("delegation/alice")),
                User("bob", bobSession, root.Child("delegation/bob"))
            ]),
            new ProbeTransport(),
            testHooks: hooks
        );
        using GalateaDelegationSessionHandle aliceHandle =
            supervisor.AttachWritableSession("alice", alice);
        using GalateaDelegationSessionHandle bobHandle =
            supervisor.AttachWritableSession("bob", bob);
        await bothEntered.Task.WaitAsync(TestDeadline);

        for (int index = 0; index < 50; index++) {
            _ = supervisor.Signal();
        }
        await Task.Delay(25);
        Assert.Equal(2, Volatile.Read(ref globalMaximum));
        Assert.All(perUserMaximum.Values,
            static value => Assert.Equal(1, value));

        supervisor.BeginShutdown();
        release.TrySetResult();
    }

    [Fact]
    public async Task OperationalFailuresAreStablePerUserUnavailable() {
        using var root = new OwnedRoot();
        string lockedSession = root.Child("sessions/locked");
        string corruptSession = root.Child("sessions/corrupt");
        using SessionJournalEngine lockedEngine =
            CreateSession(lockedSession);
        using SessionJournalEngine corruptEngine =
            CreateSession(corruptSession);
        GalateaCharacterConfig locked = User(
            "locked",
            lockedSession,
            root.Child("delegation/locked")
        );
        GalateaCharacterConfig corrupt = User(
            "corrupt",
            corruptSession,
            root.Child("delegation/corrupt")
        );
        GalateaCharacterConfig stateWithoutSession = User(
            "state-without-session",
            root.Child("sessions/missing-with-state"),
            root.Child("delegation/missing-session")
        );
        GalateaCharacterConfig unprovisioned = User(
            "unprovisioned",
            root.Child("sessions/unprovisioned"),
            root.Child("delegation/unprovisioned"),
            GalateaSessionProvisioning.ExistingOnly
        );
        GalateaConfig config = Config(root.Path, [
            locked,
            corrupt,
            stateWithoutSession,
            unprovisioned
        ]);
        using GalateaDelegationSqliteStore lockedOwner = CreateStore(
            lockedEngine,
            locked,
            config.Delegates
        );
        Directory.CreateDirectory(corrupt.DelegationStateDir);
        File.WriteAllBytes(
            Path.Combine(
                corrupt.DelegationStateDir,
                GalateaDelegationSqliteStore.LockFileName
            ),
            []
        );
        File.WriteAllText(
            Path.Combine(
                corrupt.DelegationStateDir,
                GalateaDelegationSqliteStore.DatabaseFileName
            ),
            "not sqlite"
        );
        Directory.CreateDirectory(stateWithoutSession.DelegationStateDir);

        await using var supervisor = new GalateaDelegationSupervisor(
            config,
            new ProbeTransport(),
            testHooks: NoWorkHooks(TimeSpan.FromDays(1))
        );

        AssertUnavailable(supervisor, "locked", "STORE_UNAVAILABLE");
        Assert.Equal(
            GalateaDelegationCharacterAvailability.Unavailable,
            supervisor.ReadStatus("corrupt").Availability
        );
        AssertUnavailable(
            supervisor,
            "state-without-session",
            "SESSION_MISSING"
        );
        Assert.Equal(
            GalateaDelegationCharacterAvailability.Uninitialized,
            supervisor.ReadStatus("unprovisioned").Availability
        );
        Assert.Throws<GalateaDelegationCharacterUnavailableException>(() =>
            supervisor.AttachWritableSession("locked", lockedEngine));

        GalateaConfig duplicateUserConfig = Config(root.Path, [
            User("same", root.Child("sessions/a"),
                root.Child("delegation/a")),
            User("same", root.Child("sessions/b"),
                root.Child("delegation/b"))
        ]);
        var unusedTransport = new ProbeTransport();
        Assert.Throws<InvalidOperationException>(() =>
            new GalateaDelegationSupervisor(
                duplicateUserConfig,
                unusedTransport
            ));
        Assert.Equal(0, unusedTransport.DisposeCount);
        await unusedTransport.DisposeAsync();
    }

    [Fact]
    public void ConstructionFailureReleasesAdoptedTransportAndEagerLock() {
        using var root = new OwnedRoot();
        string sessionPath = root.Child("sessions/alice");
        string statePath = root.Child("delegation/alice");
        using SessionJournalEngine engine = CreateSession(sessionPath);
        GalateaConfig config = Config(
            root.Path,
            [User("alice", sessionPath, statePath)]
        );
        using (GalateaDelegationSqliteStore created = CreateStore(
                   engine,
                   config.Characters[0],
                   config.Delegates)) { }
        var transport = new ProbeTransport();

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException>(() =>
                new GalateaDelegationSupervisor(
                    config,
                    transport,
                    testHooks: new(
                        TimeSpan.FromDays(1),
                        PulseAsync: null,
                        BeforeSchedulerStart: static () => throw new
                            InvalidOperationException(
                                "construction checkpoint failed"
                            )
                    )
                ));

        Assert.Equal("construction checkpoint failed", failure.Message);
        Assert.Equal(1, transport.DisposeCount);
        using (GalateaDelegationSqliteStore reopened =
               GalateaDelegationSqliteStore.OpenExisting(
                   statePath,
                   Owner(config.Characters[0], config.Delegates),
                   Limits(config.Delegates.CodexRoute))) {
            Assert.Equal("alice", reopened.ReadSnapshot().Owner.CharacterId);
        }

        var cleanupFailureTransport = new ProbeTransport(
            disposeFailure: new IOException("transport cleanup failed")
        );
        AggregateException aggregate = Assert.Throws<AggregateException>(
            () => new GalateaDelegationSupervisor(
                config,
                cleanupFailureTransport,
                testHooks: new(
                    TimeSpan.FromDays(1),
                    PulseAsync: null,
                    BeforeSchedulerStart: static () => throw new
                        InvalidOperationException(
                            "second construction failed"
                        )
                )
            )
        );
        Assert.Contains(aggregate.InnerExceptions,
            static exception => exception.Message
                == "second construction failed");
        Assert.Contains(aggregate.InnerExceptions,
            static exception => exception.Message
                == "transport cleanup failed");
        using GalateaDelegationSqliteStore reopenedAfterAggregate =
            GalateaDelegationSqliteStore.OpenExisting(
                statePath,
                Owner(config.Characters[0], config.Delegates),
                Limits(config.Delegates.CodexRoute)
            );
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsPulseBeforeTransportAndStore() {
        using var root = new OwnedRoot();
        string sessionPath = root.Child("sessions/alice");
        string statePath = root.Child("delegation/alice");
        using SessionJournalEngine engine = CreateSession(sessionPath);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var order = new ConcurrentQueue<string>();
        var transport = new ProbeTransport(() =>
            order.Enqueue("transport-dispose"));
        var hooks = new GalateaDelegationSupervisorTestHooks(
            TimeSpan.FromDays(1),
            async (_, _, ct) => {
                using CancellationTokenRegistration registration =
                    ct.Register(() => cancellationObserved.TrySetResult());
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                try {
                    ct.ThrowIfCancellationRequested();
                    return NoWork();
                }
                finally {
                    order.Enqueue("pulse-exit");
                }
            }
        );
        GalateaConfig config = Config(
            root.Path,
            [User("alice", sessionPath, statePath)]
        );
        var supervisor = new GalateaDelegationSupervisor(
            config,
            transport,
            testHooks: hooks
        );
        using GalateaDelegationSessionHandle handle =
            supervisor.AttachWritableSession("alice", engine);
        await entered.Task.WaitAsync(TestDeadline);

        Task dispose = supervisor.DisposeAsync().AsTask();
        await cancellationObserved.Task.WaitAsync(TestDeadline);
        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, transport.DisposeCount);
        Assert.False(supervisor.Signal());
        Assert.ThrowsAny<IOException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                statePath,
                Owner(config.Characters[0], config.Delegates),
                Limits(config.Delegates.CodexRoute)
            ));

        release.TrySetResult();
        await dispose.WaitAsync(TestDeadline);

        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(
            ["pulse-exit", "transport-dispose"],
            order.ToArray()
        );
        using GalateaDelegationSqliteStore reopened =
            GalateaDelegationSqliteStore.OpenExisting(
                statePath,
                Owner(config.Characters[0], config.Delegates),
                Limits(config.Delegates.CodexRoute)
            );
        Assert.Equal("alice", reopened.ReadSnapshot().Owner.CharacterId);
    }

    private static void AssertUnavailable(
        GalateaDelegationSupervisor supervisor,
        string userId,
        string code
    ) {
        GalateaDelegationCharacterStatus status = supervisor.ReadStatus(userId);
        Assert.Equal(GalateaDelegationCharacterAvailability.Unavailable,
            status.Availability);
        Assert.Equal(code, status.UnavailableCode);
    }

    private static GalateaDelegationSupervisorTestHooks NoWorkHooks(
        TimeSpan interval
    ) => new(
        interval,
        static (_, _, _) => Task.FromResult(NoWork())
    );

    private static GalateaDurableDelegationPulseResult NoWork() => new(
        GalateaDurableDelegationPulseStep.NoWork
    );

    private static async Task WaitUntilAsync(Func<bool> predicate) {
        using var deadline = new CancellationTokenSource(TestDeadline);
        while (!predicate()) {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static void SetMaximum(ref int target, int candidate) {
        int current;
        do {
            current = Volatile.Read(ref target);
            if (candidate <= current) { return; }
        } while (Interlocked.CompareExchange(
            ref target,
            candidate,
            current
        ) != current);
    }

    private static SessionJournalEngine CreateSession(string path) =>
        SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system prompt",
                "openai-chat/strict"
            )
        );

    private static GalateaConfig Config(
        string root,
        IReadOnlyList<GalateaCharacterConfig> users,
        bool maintenanceMode = false
    ) => new(
        Characters: users,
        Players: [],
        Connections: [],
        SelectableConnectionIds: [],
        InputNormalizerConnectionId: null,
        Delegates: GalateaDelegateTestConfiguration.Create(root),
        MaintenanceMode: maintenanceMode
    );

    private static GalateaCharacterConfig User(
        string userId,
        string sessionPath,
        string statePath,
        GalateaSessionProvisioning provisioning =
            GalateaSessionProvisioning.CreateIfMissing
    ) => new(
        userId,
        new GalateaCharacterName("Galatea"),
        sessionPath,
        statePath,
        statePath + "-character-memory",
        Directory.CreateDirectory(statePath + "-home").FullName,
        provisioning,
        SystemPrompt: "prompt",
        DefaultConnectionId: "unused"
    );

    private static GalateaDelegationSqliteStore CreateStore(
        SessionJournalEngine engine,
        GalateaCharacterConfig user,
        GalateaDelegateConfig delegates
    ) {
        Directory.CreateDirectory(
            Path.GetDirectoryName(user.DelegationStateDir)
                ?? throw new InvalidOperationException(
                    "Delegation test path has no parent."
                )
        );
        return GalateaDelegationSqliteStore.CreateNew(
            user.DelegationStateDir,
            Owner(user, delegates),
            new GalateaDelegationStoreBaseline(
                engine.ReadView.ReadPhysicalAppendFrontier(),
                engine.ReadView.ReadCurrentHead() is { } head
                    ? EventAddressTextCodec.Format(head)
                    : null
            ),
            Limits(delegates.CodexRoute)
        );
    }

    private static GalateaDelegationStoreOwner Owner(
        GalateaCharacterConfig user,
        GalateaDelegateConfig delegates
    ) => new(
        user.CharacterId,
        GalateaDelegationSupervisor.CreateSessionRepositoryId(
            user.SessionDir
        )
    );

    private static GalateaDelegationStoreLimits Limits(
        GalateaDelegateRouteConfig route
    ) => new(
        route.MaximumQueuedMails,
        route.MaximumTaskUtf8Bytes,
        route.MaximumReplyUtf8Bytes,
        route.MaximumInboxReplies,
        route.MaximumInboxUtf8Bytes
    );

    private sealed class ProbeTransport(
        Action? onDispose = null,
        Exception? disposeFailure = null
    ) : IGalateaDurableDelegateTransport {
        private int _disposeCount;
        private int _externalCallCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal int ExternalCallCount => Volatile.Read(
            ref _externalCallCount);

        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request,
            CancellationToken ct
        ) {
            Interlocked.Increment(ref _externalCallCount);
            throw new InvalidOperationException(
                "Unexpected external ensure-binding call."
            );
        }

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request,
            CancellationToken ct
        ) {
            Interlocked.Increment(ref _externalCallCount);
            throw new InvalidOperationException(
                "Unexpected external start-turn call."
            );
        }

        public Task<GalateaDelegateDispatchInspection>
            InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest request,
                CancellationToken ct
            ) {
            Interlocked.Increment(ref _externalCallCount);
            throw new InvalidOperationException(
                "Unexpected external inspect-dispatch call."
            );
        }

        public ValueTask DisposeAsync() {
            if (Interlocked.Increment(ref _disposeCount) == 1) {
                onDispose?.Invoke();
                if (disposeFailure is not null) {
                    return ValueTask.FromException(disposeFailure);
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OwnedRoot : IDisposable {
        internal OwnedRoot() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-galatea-supervisor-"
                    + Guid.NewGuid().ToString("N")
            );
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(
                Path
            );
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string Child(string relative) =>
            System.IO.Path.GetFullPath(relative, Path);

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(Path);
    }
}
