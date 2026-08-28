using System.Collections.Concurrent;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationSupervisorTests {
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);

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
            GalateaDelegationUserAvailability.Uninitialized,
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
            GalateaDelegationUserAvailability.Writable,
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
                   writableConfig.Users[0],
                   writableConfig.Delegates)) { }

        var writableTransport = new ProbeTransport();
        await using (var supervisor = new GalateaDelegationSupervisor(
                         writableConfig,
                         writableTransport,
                         testHooks: NoWorkHooks(TimeSpan.FromDays(1)))) {
            Assert.Equal(
                GalateaDelegationUserAvailability.Writable,
                supervisor.ReadStatus("alice").Availability
            );
            Assert.ThrowsAny<IOException>(() =>
                GalateaDelegationSqliteStore.OpenExisting(
                    statePath,
                    Owner(writableConfig.Users[0],
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
                GalateaDelegationUserAvailability.ReadOnly,
                supervisor.ReadStatus("alice").Availability
            );
            Assert.False(supervisor.Signal());
            await Task.Delay(50);
            Assert.Equal(0, Volatile.Read(ref pulseCount));
            GalateaDelegationUserUnavailableException failure =
                Assert.Throws<GalateaDelegationUserUnavailableException>(
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
        GalateaUserConfig locked = User(
            "locked",
            lockedSession,
            root.Child("delegation/locked")
        );
        GalateaUserConfig corrupt = User(
            "corrupt",
            corruptSession,
            root.Child("delegation/corrupt")
        );
        GalateaUserConfig stateWithoutSession = User(
            "state-without-session",
            root.Child("sessions/missing-with-state"),
            root.Child("delegation/missing-session")
        );
        GalateaUserConfig unprovisioned = User(
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
            GalateaDelegationUserAvailability.Unavailable,
            supervisor.ReadStatus("corrupt").Availability
        );
        AssertUnavailable(
            supervisor,
            "state-without-session",
            "SESSION_MISSING"
        );
        Assert.Equal(
            GalateaDelegationUserAvailability.Uninitialized,
            supervisor.ReadStatus("unprovisioned").Availability
        );
        Assert.Throws<GalateaDelegationUserUnavailableException>(() =>
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
                   config.Users[0],
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
                   Owner(config.Users[0], config.Delegates),
                   Limits(config.Delegates.CodexRoute))) {
            Assert.Equal("alice", reopened.ReadSnapshot().Owner.UserId);
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
                Owner(config.Users[0], config.Delegates),
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
                Owner(config.Users[0], config.Delegates),
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
                Owner(config.Users[0], config.Delegates),
                Limits(config.Delegates.CodexRoute)
            );
        Assert.Equal("alice", reopened.ReadSnapshot().Owner.UserId);
    }

    private static void AssertUnavailable(
        GalateaDelegationSupervisor supervisor,
        string userId,
        string code
    ) {
        GalateaDelegationUserStatus status = supervisor.ReadStatus(userId);
        Assert.Equal(GalateaDelegationUserAvailability.Unavailable,
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
        IReadOnlyList<GalateaUserConfig> users,
        bool maintenanceMode = false
    ) => new(
        Users: users,
        Connections: [],
        DefaultConnectionId: "unused",
        SelectableConnectionIds: [],
        InputNormalizerConnectionId: null,
        Delegates: GalateaDelegateTestConfiguration.Create(root),
        MaintenanceMode: maintenanceMode
    );

    private static GalateaUserConfig User(
        string userId,
        string sessionPath,
        string statePath,
        GalateaSessionProvisioning provisioning =
            GalateaSessionProvisioning.CreateIfMissing
    ) => new(
        userId,
        "pw",
        new GalateaCharacterName("Galatea"),
        new GalateaPlayerName("Player"),
        sessionPath,
        statePath,
        provisioning,
        SystemPrompt: "prompt"
    );

    private static GalateaDelegationSqliteStore CreateStore(
        SessionJournalEngine engine,
        GalateaUserConfig user,
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
        GalateaUserConfig user,
        GalateaDelegateConfig delegates
    ) => new(
        user.UserId,
        GalateaDelegationSupervisor.CreateSessionRepositoryId(
            user.SessionDir
        ),
        GalateaDelegationDurableContract.CreateRoutePolicyFingerprint(
            delegates.CodexRoute
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
