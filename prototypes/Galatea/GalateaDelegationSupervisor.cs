using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Atelia.Diagnostics;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal enum GalateaDelegationUserAvailability {
    Uninitialized,
    Writable,
    ReadOnly,
    Unavailable
}

internal sealed record GalateaDelegationUserStatus(
    string UserId,
    GalateaDelegationUserAvailability Availability,
    string? UnavailableCode
);

internal sealed class GalateaDelegationUserUnavailableException
    : InvalidOperationException {
    internal GalateaDelegationUserUnavailableException(
        string userId,
        string code
    ) : base(
        $"Durable delegation for user '{userId}' is unavailable: {code}."
    ) {
        UserId = userId;
        Code = code;
    }

    internal string UserId { get; }
    internal string Code { get; }
}

internal sealed record GalateaDelegationSupervisorTestHooks(
    TimeSpan? PulseInterval = null,
    Func<
        string,
        GalateaDurableDelegationDriver,
        CancellationToken,
        Task<GalateaDurableDelegationPulseResult>
    >? PulseAsync = null,
    Action? BeforeSchedulerStart = null
);

/// <summary>
/// Borrowed per-session access to a supervisor-owned durable store. Disposing
/// the handle never disposes the store or its process-lifetime lock.
/// </summary>
internal sealed class GalateaDelegationSessionHandle : IDisposable {
    private Attachment? _attachment;

    internal GalateaDelegationSessionHandle(
        GalateaDelegationSupervisor supervisor,
        GalateaDelegationSupervisor.UserSlot slot,
        object attachmentToken
    ) {
        _attachment = new Attachment(supervisor, slot, attachmentToken);
    }

    internal GalateaDelegationSqliteStore Store =>
        (Volatile.Read(ref _attachment)
            ?? throw new ObjectDisposedException(GetType().Name)).Slot.Store;

    internal bool Signal() =>
        (Volatile.Read(ref _attachment)
            ?? throw new ObjectDisposedException(GetType().Name))
            .Supervisor.Signal();

    public void Dispose() {
        Attachment? attachment = Interlocked.Exchange(
            ref _attachment,
            null
        );
        if (attachment is not null) {
            attachment.Supervisor.DetachWritableSession(
                attachment.Slot,
                attachment.Token
            );
        }
    }

    private sealed record Attachment(
        GalateaDelegationSupervisor Supervisor,
        GalateaDelegationSupervisor.UserSlot Slot,
        object Token
    );
}

/// <summary>
/// Host-wide owner for durable delegation stores, the single V3 transport,
/// and bounded pulse scheduling. Composition must finish every other fallible
/// preflight before construction: writable existing slots are scheduled as
/// soon as this constructor succeeds.
/// </summary>
internal sealed class GalateaDelegationSupervisor : IAsyncDisposable {
    private const string LogCategory = "Galatea.Delegation.Supervisor";
    private const string SessionRepositoryPrefix = "gdsr1-";
    private static readonly TimeSpan DefaultPulseInterval =
        TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly object _lifecycleGate = new();
    private readonly Dictionary<string, UserSlot> _slots;
    private readonly IGalateaDurableDelegateTransport _transport;
    private readonly Channel<byte> _signals;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _inFlightPulses = [];
    private readonly GalateaDelegationSupervisorTestHooks _testHooks;
    private readonly TimeProvider _timeProvider;
    private readonly bool _maintenanceMode;
    private readonly Task _timerTask;
    private readonly Task _consumerTask;
    private bool _shutdownBegun;
    private Task? _disposeTask;

    internal GalateaDelegationSupervisor(
        GalateaConfig config,
        IGalateaDurableDelegateTransport? transport = null,
        TimeProvider? timeProvider = null,
        GalateaDelegationSupervisorTestHooks? testHooks = null,
        Func<GalateaDelegateConfig, IGalateaDurableDelegateTransport>?
            transportFactory = null
    ) {
        ArgumentNullException.ThrowIfNull(config);
        if (transport is not null && transportFactory is not null) {
            throw new ArgumentException(
                "Specify either a durable transport or a transport factory."
            );
        }
        GalateaDelegationDurableFiles.RequireLinux();
        GalateaConfigValidation.RequireValidStorageTopology(
            config.Users,
            config.CallLogDir
        );
        GalateaDelegateConfig delegates =
            GalateaDelegateConfigReader.Validate(config.Delegates);
        RequireGlobalUserIdentity(config.Users);

        _maintenanceMode = config.MaintenanceMode;
        _testHooks = testHooks
            ?? new GalateaDelegationSupervisorTestHooks();
        TimeSpan pulseInterval = _testHooks.PulseInterval
            ?? DefaultPulseInterval;
        if (pulseInterval <= TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(
                nameof(testHooks),
                "The delegation pulse interval must be positive."
            );
        }
        TimeProvider effectiveTimeProvider =
            timeProvider ?? TimeProvider.System;
        _timeProvider = effectiveTimeProvider;
        GalateaDelegateRouteConfig route = delegates.CodexRoute;
        string routeFingerprint = GalateaDelegationDurableContract
            .CreateRoutePolicyFingerprint(route);
        GalateaDelegationStoreLimits limits = CreateLimits(route);

        var openedSlots = new Dictionary<string, UserSlot>(
            config.Users.Count,
            StringComparer.Ordinal
        );
        IGalateaDurableDelegateTransport? ownedTransport = null;
        try {
            foreach (GalateaUserConfig user in config.Users) {
                string sessionDirectory = RequireCanonicalAbsoluteDirectory(
                    user.SessionDir,
                    $"sessionDir for user '{user.UserId}'"
                );
                string stateDirectory = RequireCanonicalAbsoluteDirectory(
                    user.DelegationStateDir,
                    $"delegationStateDir for user '{user.UserId}'"
                );
                var owner = new GalateaDelegationStoreOwner(
                    user.UserId,
                    CreateSessionRepositoryId(sessionDirectory),
                    routeFingerprint
                );
                UserSlot slot = CreateSlot(
                    user,
                    sessionDirectory,
                    stateDirectory,
                    owner,
                    limits,
                    config.MaintenanceMode
                );
                openedSlots.Add(user.UserId, slot);
            }

            ownedTransport = transport
                ?? transportFactory?.Invoke(delegates)
                ?? new GalateaCodexDurableSidecarClient(delegates);
            foreach (UserSlot slot in openedSlots.Values) {
                slot.CreateDriverIfWritable(
                    ownedTransport,
                    routeFingerprint,
                    effectiveTimeProvider
                );
            }
            Channel<byte> signals = Channel.CreateBounded<byte>(
                new BoundedChannelOptions(1) {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropWrite
                }
            );
            _testHooks.BeforeSchedulerStart?.Invoke();

            _slots = openedSlots;
            _transport = ownedTransport;
            _signals = signals;
            if (_maintenanceMode) {
                _timerTask = Task.CompletedTask;
                _consumerTask = Task.CompletedTask;
            }
            else {
                _timerTask = ProduceFallbackSignalsAsync(
                    pulseInterval,
                    effectiveTimeProvider
                );
                _consumerTask = ConsumeSignalsAsync();
                _ = Signal();
            }
        }
        catch (Exception constructionFailure) when (
            GalateaExceptionClassifier.IsNonFatal(constructionFailure)) {
            var cleanupFailures = new List<Exception>();
            if (ownedTransport is not null) {
                try {
                    ownedTransport.DisposeAsync().AsTask()
                        .GetAwaiter().GetResult();
                }
                catch (Exception cleanupFailure) when (
                    GalateaExceptionClassifier.IsNonFatal(cleanupFailure)) {
                    cleanupFailures.Add(cleanupFailure);
                }
            }
            foreach (UserSlot slot in openedSlots.Values) {
                try {
                    slot.DisposeStore();
                }
                catch (Exception cleanupFailure) when (
                    GalateaExceptionClassifier.IsNonFatal(cleanupFailure)) {
                    cleanupFailures.Add(cleanupFailure);
                }
            }
            if (cleanupFailures.Count == 0) { throw; }
            throw new AggregateException(
                [constructionFailure, .. cleanupFailures]
            );
        }
    }

    internal bool IsMaintenanceMode => _maintenanceMode;

    internal IReadOnlyList<GalateaDelegationUserStatus> ReadStatuses() =>
        _slots.Values
            .Select(static slot => slot.ReadStatus())
            .OrderBy(static status => status.UserId, StringComparer.Ordinal)
            .ToArray();

    internal GalateaDelegationUserStatus ReadStatus(string userId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return GetSlot(userId).ReadStatus();
    }

    /// <summary>
    /// Pure observation facade. It intentionally does not initialize or
    /// attach a session, signal the scheduler, or advance delegation state.
    /// </summary>
    internal GalateaMailboxStatusProjection ReadMailboxStatus(
        string userId
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return GetSlot(userId).ReadMailboxStatus();
    }

    internal GalateaDelegationSessionHandle AttachWritableSession(
        string userId,
        SessionJournalEngine engine
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(engine);
        lock (_lifecycleGate) {
            if (_shutdownBegun) {
                throw new ObjectDisposedException(GetType().Name);
            }
            if (_maintenanceMode) {
                throw new GalateaDelegationUserUnavailableException(
                    userId,
                    "MAINTENANCE_READ_ONLY"
                );
            }
            UserSlot slot = GetSlot(userId);
            object attachmentToken = slot.AttachWritableSession(
                engine,
                _transport,
                _timeProvider
            );
            var handle = new GalateaDelegationSessionHandle(
                this,
                slot,
                attachmentToken
            );
            _ = Signal();
            return handle;
        }
    }

    internal bool Signal() {
        lock (_lifecycleGate) {
            if (_shutdownBegun || _maintenanceMode) { return false; }
            return _signals.Writer.TryWrite(0);
        }
    }

    internal void DetachWritableSession(
        UserSlot slot,
        object attachmentToken
    ) {
        lock (_lifecycleGate) {
            slot.DetachWritableSession(attachmentToken);
        }
    }

    internal void BeginShutdown() {
        lock (_lifecycleGate) {
            if (_shutdownBegun) { return; }
            _shutdownBegun = true;
            _signals.Writer.TryComplete();
            _shutdown.Cancel();
        }
        DebugUtil.Info(
            LogCategory,
            "Durable delegation supervisor shutdown started."
        );
    }

    public ValueTask DisposeAsync() {
        lock (_lifecycleGate) {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    internal static string CreateSessionRepositoryId(
        string sessionDirectory
    ) {
        string canonical = RequireCanonicalAbsoluteDirectory(
            sessionDirectory,
            nameof(sessionDirectory)
        );
        byte[] utf8 = StrictUtf8.GetBytes(canonical);
        return SessionRepositoryPrefix
            + Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
    }

    private static UserSlot CreateSlot(
        GalateaUserConfig user,
        string sessionDirectory,
        string stateDirectory,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits,
        bool maintenanceMode
    ) {
        if (!Path.Exists(stateDirectory)) {
            return UserSlot.Uninitialized(
                user,
                sessionDirectory,
                stateDirectory,
                owner,
                limits
            );
        }
        if (!Directory.Exists(sessionDirectory)) {
            return UserSlot.Unavailable(
                user,
                sessionDirectory,
                stateDirectory,
                owner,
                limits,
                "SESSION_MISSING"
            );
        }

        try {
            GalateaDelegationSqliteStore store = maintenanceMode
                ? GalateaDelegationSqliteStore.OpenExistingReadOnly(
                    stateDirectory,
                    owner,
                    limits
                )
                : GalateaDelegationSqliteStore.OpenExisting(
                    stateDirectory,
                    owner,
                    limits
                );
            return UserSlot.Opened(
                user,
                sessionDirectory,
                stateDirectory,
                owner,
                limits,
                store,
                maintenanceMode
            );
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            string code = exception is InvalidDataException
                ? "STORE_INVALID"
                : "STORE_UNAVAILABLE";
            DebugUtil.Warning(
                LogCategory,
                "Durable delegation store unavailable: "
                    + $"user={Safe(user.UserId)}, code={code}, "
                    + $"exception={exception.GetType().Name}."
            );
            return UserSlot.Unavailable(
                user,
                sessionDirectory,
                stateDirectory,
                owner,
                limits,
                code
            );
        }
    }

    private static void RequireGlobalUserIdentity(
        IReadOnlyList<GalateaUserConfig> users
    ) {
        if (users.Count == 0) {
            throw new InvalidOperationException(
                "Durable delegation requires at least one configured user."
            );
        }
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (GalateaUserConfig user in users) {
            if (string.IsNullOrWhiteSpace(user.UserId)
                || !userIds.Add(user.UserId)) {
                throw new InvalidOperationException(
                    "Durable delegation requires exact unique userId values."
                );
            }
            _ = RequireCanonicalAbsoluteDirectory(
                user.SessionDir,
                $"sessionDir for user '{user.UserId}'"
            );
            _ = RequireCanonicalAbsoluteDirectory(
                user.DelegationStateDir,
                $"delegationStateDir for user '{user.UserId}'"
            );
        }
    }

    private static string RequireCanonicalAbsoluteDirectory(
        string path,
        string description
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) {
            throw new InvalidOperationException(
                $"Durable delegation {description} must be absolute."
            );
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static GalateaDelegationStoreLimits CreateLimits(
        GalateaDelegateRouteConfig route
    ) => new(
        route.MaximumQueuedMails,
        route.MaximumTaskUtf8Bytes,
        route.MaximumReplyUtf8Bytes,
        route.MaximumInboxReplies,
        route.MaximumInboxUtf8Bytes
    );

    private UserSlot GetSlot(string userId) =>
        _slots.TryGetValue(userId, out UserSlot? slot)
            ? slot
            : throw new KeyNotFoundException(
                $"Unknown Galatea delegation user '{userId}'."
            );

    private async Task ProduceFallbackSignalsAsync(
        TimeSpan interval,
        TimeProvider timeProvider
    ) {
        using var timer = new PeriodicTimer(interval, timeProvider);
        try {
            while (await timer.WaitForNextTickAsync(_shutdown.Token)
                       .ConfigureAwait(false)) {
                _ = Signal();
            }
        }
        catch (OperationCanceledException) when (
            _shutdown.IsCancellationRequested) { }
    }

    private async Task ConsumeSignalsAsync() {
        try {
            while (await _signals.Reader
                       .WaitToReadAsync(_shutdown.Token)
                       .ConfigureAwait(false)) {
                while (_signals.Reader.TryRead(out _)) { }
                SchedulePulses();
            }
        }
        catch (OperationCanceledException) when (
            _shutdown.IsCancellationRequested) { }
    }

    private void SchedulePulses() {
        lock (_lifecycleGate) {
            if (_shutdownBegun) { return; }
            foreach (UserSlot slot in _slots.Values) {
                if (!slot.TryBeginPulse()) { continue; }
                Task<bool> task = RunPulseAsync(slot, _shutdown.Token);
                _inFlightPulses.Add(task);
                _ = task.ContinueWith(
                    static (completed, state) =>
                        ((GalateaDelegationSupervisor)state!)
                            .OnPulseCompleted(completed),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
            }
        }
    }

    private async Task<bool> RunPulseAsync(
        UserSlot slot,
        CancellationToken cancellationToken
    ) {
        bool resignal = false;
        try {
            GalateaDurableDelegationPulseResult result =
                await slot.PulseAsync(_testHooks, cancellationToken)
                    .ConfigureAwait(false);
            resignal = ShouldResignal(result.Step);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            resignal = false;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            slot.MarkUnavailable("PULSE_FAILED");
            DebugUtil.Error(
                LogCategory,
                "Durable delegation pulse failed closed: "
                    + $"user={Safe(slot.UserId)}, code=PULSE_FAILED, "
                    + $"exception={exception.GetType().Name}."
            );
            resignal = false;
        }
        finally {
            resignal |= slot.EndPulse();
        }
        return resignal;
    }

    private void OnPulseCompleted(Task<bool> completed) {
        bool resignal = false;
        if (completed.Status == TaskStatus.RanToCompletion) {
            resignal = completed.Result;
        }
        else if (completed.IsFaulted) {
            _ = completed.Exception;
        }
        lock (_lifecycleGate) {
            _inFlightPulses.Remove(completed);
        }
        if (resignal) { _ = Signal(); }
    }

    private static bool ShouldResignal(
        GalateaDurableDelegationPulseStep step
    ) => step is
        GalateaDurableDelegationPulseStep.QueuedPreflightFailed
        or GalateaDurableDelegationPulseStep.BindingClaimed
        or GalateaDurableDelegationPulseStep.BindingEstablished
        or GalateaDurableDelegationPulseStep.MailAccepted
        or GalateaDurableDelegationPulseStep.RecoveredStarted
        or GalateaDurableDelegationPulseStep.TerminalCompleted
        or GalateaDurableDelegationPulseStep.TerminalFailed;

    private async Task DisposeCoreAsync() {
        BeginShutdown();
        var failures = new List<Exception>();
        await ObserveAsync(
                _timerTask,
                "fallback-signal-producer",
                failures
            )
            .ConfigureAwait(false);
        await ObserveAsync(
                _consumerTask,
                "signal-consumer",
                failures
            )
            .ConfigureAwait(false);

        Task[] pulses;
        lock (_lifecycleGate) {
            pulses = _inFlightPulses.ToArray();
        }
        if (pulses.Length != 0) {
            await ObserveAsync(
                    Task.WhenAll(pulses),
                    "in-flight-pulses",
                    failures
                )
                .ConfigureAwait(false);
        }
        foreach (UserSlot slot in _slots.Values) {
            slot.LogActiveDispatchPreservedForColdRestart();
        }
        try {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            failures.Add(exception);
            LogCleanupFailure("transport", exception);
        }
        foreach (UserSlot slot in _slots.Values) {
            try {
                slot.DisposeStore();
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                failures.Add(exception);
                LogCleanupFailure(
                    $"store user={Safe(slot.UserId)}",
                    exception
                );
            }
        }
        _shutdown.Dispose();

        if (failures.Count == 0) {
            DebugUtil.Info(
                LogCategory,
                "Durable delegation supervisor shutdown completed.",
                eventKind: DebugEventKind.Success
            );
            return;
        }
        DebugUtil.Warning(
            LogCategory,
            "Durable delegation supervisor shutdown completed with "
                + $"cleanupFailures={failures.Count}.",
            eventKind: DebugEventKind.Failure
        );
        if (failures.Count == 1) { throw failures[0]; }
        if (failures.Count > 1) { throw new AggregateException(failures); }
    }

    private static async Task ObserveAsync(
        Task task,
        string component,
        List<Exception> failures
    ) {
        try {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            failures.Add(exception);
            LogCleanupFailure(component, exception);
        }
    }

    private static void LogCleanupFailure(
        string component,
        Exception exception
    ) => DebugUtil.Warning(
        LogCategory,
        "Durable delegation supervisor cleanup failed: "
            + $"component={component}, "
            + $"exception={exception.GetType().Name}.",
        exception,
        DebugEventKind.Failure
    );

    private static string Safe(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Length <= 128 ? value : value[..128];

    internal sealed class UserSlot {
        private readonly object _gate = new();
        private readonly GalateaUserConfig _user;
        private readonly string _sessionDirectory;
        private readonly string _stateDirectory;
        private readonly GalateaDelegationStoreOwner _owner;
        private readonly GalateaDelegationStoreLimits _limits;
        private GalateaDelegationSqliteStore? _store;
        private GalateaDurableDelegationDriver? _driver;
        private SessionJournalEngine? _attachedEngine;
        private object? _attachmentToken;
        private GalateaDelegationUserAvailability _availability;
        private string? _unavailableCode;
        private bool _pulseInFlight;
        private bool _pulseRequested;

        private UserSlot(
            GalateaUserConfig user,
            string sessionDirectory,
            string stateDirectory,
            GalateaDelegationStoreOwner owner,
            GalateaDelegationStoreLimits limits,
            GalateaDelegationUserAvailability availability,
            string? unavailableCode,
            GalateaDelegationSqliteStore? store
        ) {
            _user = user;
            _sessionDirectory = sessionDirectory;
            _stateDirectory = stateDirectory;
            _owner = owner;
            _limits = limits;
            _availability = availability;
            _unavailableCode = unavailableCode;
            _store = store;
        }

        internal string UserId => _user.UserId;

        internal GalateaDelegationSqliteStore Store {
            get {
                lock (_gate) {
                    return _store ?? throw new
                        GalateaDelegationUserUnavailableException(
                            UserId,
                            _unavailableCode ?? "STORE_UNINITIALIZED"
                        );
                }
            }
        }

        internal static UserSlot Uninitialized(
            GalateaUserConfig user,
            string sessionDirectory,
            string stateDirectory,
            GalateaDelegationStoreOwner owner,
            GalateaDelegationStoreLimits limits
        ) => new(
            user,
            sessionDirectory,
            stateDirectory,
            owner,
            limits,
            GalateaDelegationUserAvailability.Uninitialized,
            unavailableCode: null,
            store: null
        );

        internal static UserSlot Opened(
            GalateaUserConfig user,
            string sessionDirectory,
            string stateDirectory,
            GalateaDelegationStoreOwner owner,
            GalateaDelegationStoreLimits limits,
            GalateaDelegationSqliteStore store,
            bool readOnly
        ) => new(
            user,
            sessionDirectory,
            stateDirectory,
            owner,
            limits,
            readOnly
                ? GalateaDelegationUserAvailability.ReadOnly
                : GalateaDelegationUserAvailability.Writable,
            unavailableCode: null,
            store
        );

        internal static UserSlot Unavailable(
            GalateaUserConfig user,
            string sessionDirectory,
            string stateDirectory,
            GalateaDelegationStoreOwner owner,
            GalateaDelegationStoreLimits limits,
            string code
        ) => new(
            user,
            sessionDirectory,
            stateDirectory,
            owner,
            limits,
            GalateaDelegationUserAvailability.Unavailable,
            code,
            store: null
        );

        internal GalateaDelegationUserStatus ReadStatus() {
            lock (_gate) {
                return new(UserId, _availability, _unavailableCode);
            }
        }

        internal GalateaMailboxStatusProjection ReadMailboxStatus() {
            lock (_gate) {
                if (_availability
                    == GalateaDelegationUserAvailability.Unavailable) {
                    return GalateaMailboxStatusProjection.Unavailable(
                        _unavailableCode ?? "STORE_UNAVAILABLE"
                    );
                }
                if (_store is null) {
                    // A missing store cannot prove an initialized empty
                    // mailbox. Observing it must not initialize a session.
                    return GalateaMailboxStatusProjection.Unavailable(
                        "STORE_UNINITIALIZED"
                    );
                }
                try {
                    return _store.ReadMailboxStatus();
                }
                catch (Exception exception) when (
                    GalateaExceptionClassifier.IsNonFatal(exception)) {
                    DebugUtil.Warning(
                        LogCategory,
                        "Durable mailbox status read failed: "
                            + $"user={Safe(UserId)}, "
                            + "code=STORE_READ_FAILED, "
                            + $"exception={exception.GetType().Name}.",
                        exception,
                        DebugEventKind.Failure
                    );
                    return GalateaMailboxStatusProjection.Unavailable(
                        "STORE_READ_FAILED"
                    );
                }
            }
        }

        internal void CreateDriverIfWritable(
            IGalateaDurableDelegateTransport transport,
            string routeFingerprint,
            TimeProvider timeProvider
        ) {
            lock (_gate) {
                if (_availability
                    != GalateaDelegationUserAvailability.Writable) {
                    return;
                }
                try {
                    _driver ??= new GalateaDurableDelegationDriver(
                        _store ?? throw new InvalidOperationException(
                            "A writable delegation slot has no store."
                        ),
                        transport,
                        routeFingerprint,
                        timeProvider
                    );
                }
                catch (Exception exception) when (
                    GalateaExceptionClassifier.IsNonFatal(exception)) {
                    _availability =
                        GalateaDelegationUserAvailability.Unavailable;
                    _unavailableCode = "DRIVER_CREATE_FAILED";
                    DebugUtil.Error(
                        LogCategory,
                        "Durable delegation driver creation failed closed: "
                            + $"user={Safe(UserId)}, "
                            + "code=DRIVER_CREATE_FAILED, "
                            + $"exception={exception.GetType().Name}."
                    );
                }
            }
        }

        internal object AttachWritableSession(
            SessionJournalEngine engine,
            IGalateaDurableDelegateTransport transport,
            TimeProvider timeProvider
        ) {
            lock (_gate) {
                if (_availability
                    == GalateaDelegationUserAvailability.Unavailable) {
                    throw new GalateaDelegationUserUnavailableException(
                        UserId,
                        _unavailableCode ?? "STORE_UNAVAILABLE"
                    );
                }
                if (_availability
                    == GalateaDelegationUserAvailability.ReadOnly) {
                    throw new GalateaDelegationUserUnavailableException(
                        UserId,
                        "MAINTENANCE_READ_ONLY"
                    );
                }
                if (_attachmentToken is not null) {
                    throw new GalateaDelegationUserUnavailableException(
                        UserId,
                        "SESSION_ALREADY_ATTACHED"
                    );
                }
                string engineDirectory = RequireCanonicalAbsoluteDirectory(
                    engine.Path,
                    $"attached session for user '{UserId}'"
                );
                if (!string.Equals(
                        engineDirectory,
                        _sessionDirectory,
                        StringComparison.Ordinal)) {
                    throw new GalateaDelegationUserUnavailableException(
                        UserId,
                        "SESSION_IDENTITY_MISMATCH"
                    );
                }
                if (_availability
                    == GalateaDelegationUserAvailability.Uninitialized) {
                    CreateBaselineStore(engine);
                }
                try {
                    _driver ??= new GalateaDurableDelegationDriver(
                        _store ?? throw new InvalidOperationException(
                            "An attached delegation slot has no store."
                        ),
                        transport,
                        _owner.RoutePolicyFingerprint,
                        timeProvider
                    );
                }
                catch (Exception exception) when (
                    GalateaExceptionClassifier.IsNonFatal(exception)) {
                    _availability =
                        GalateaDelegationUserAvailability.Unavailable;
                    _unavailableCode = "DRIVER_CREATE_FAILED";
                    DebugUtil.Error(
                        LogCategory,
                        "Durable delegation driver creation failed closed: "
                            + $"user={Safe(UserId)}, "
                            + "code=DRIVER_CREATE_FAILED, "
                            + $"exception={exception.GetType().Name}."
                    );
                    throw new GalateaDelegationUserUnavailableException(
                        UserId,
                        _unavailableCode
                    );
                }
                _attachedEngine = engine;
                _attachmentToken = new object();
                _availability = GalateaDelegationUserAvailability.Writable;
                _unavailableCode = null;
                return _attachmentToken;
            }
        }

        internal void DetachWritableSession(object attachmentToken) {
            lock (_gate) {
                if (!ReferenceEquals(_attachmentToken, attachmentToken)) {
                    return;
                }
                _attachedEngine = null;
                _attachmentToken = null;
            }
        }

        private void CreateBaselineStore(SessionJournalEngine engine) {
            EventJournalPhysicalAppendFrontier frontier = engine.ReadView
                .ReadPhysicalAppendFrontier();
            string? selectedHead = engine.ReadView.ReadCurrentHead() is { } head
                ? EventAddressTextCodec.Format(head)
                : null;
            try {
                string parent = Path.GetDirectoryName(_stateDirectory)
                    ?? throw new InvalidOperationException(
                        "Delegation state directory has no parent."
                    );
                Directory.CreateDirectory(parent);
                _store = GalateaDelegationSqliteStore.CreateNew(
                    _stateDirectory,
                    _owner,
                    new GalateaDelegationStoreBaseline(
                        frontier,
                        selectedHead
                    ),
                    _limits
                );
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                _availability =
                    GalateaDelegationUserAvailability.Unavailable;
                _unavailableCode = "STORE_CREATE_FAILED";
                DebugUtil.Error(
                    LogCategory,
                    "Durable delegation baseline creation failed closed: "
                        + $"user={Safe(UserId)}, "
                        + "code=STORE_CREATE_FAILED, "
                        + $"exception={exception.GetType().Name}."
                );
                throw new GalateaDelegationUserUnavailableException(
                    UserId,
                    _unavailableCode
                );
            }
        }

        internal bool TryBeginPulse() {
            lock (_gate) {
                if (_availability
                        != GalateaDelegationUserAvailability.Writable
                    || _driver is null) {
                    return false;
                }
                if (_pulseInFlight) {
                    _pulseRequested = true;
                    return false;
                }
                _pulseInFlight = true;
                _pulseRequested = false;
                return true;
            }
        }

        internal Task<GalateaDurableDelegationPulseResult> PulseAsync(
            GalateaDelegationSupervisorTestHooks testHooks,
            CancellationToken cancellationToken
        ) {
            GalateaDurableDelegationDriver driver;
            lock (_gate) {
                driver = _driver ?? throw new InvalidOperationException(
                    "A claimed delegation pulse has no driver."
                );
            }
            return testHooks.PulseAsync is { } pulse
                ? pulse(UserId, driver, cancellationToken)
                : driver.PulseAsync(cancellationToken);
        }

        internal bool EndPulse() {
            lock (_gate) {
                _pulseInFlight = false;
                bool requested = _pulseRequested;
                _pulseRequested = false;
                return requested;
            }
        }

        [Conditional("DEBUG")]
        internal void LogActiveDispatchPreservedForColdRestart() {
            try {
                GalateaDelegationStateSnapshot? snapshot;
                lock (_gate) {
                    snapshot = _store?.ReadSnapshot();
                }
                if (snapshot?.Route.ActiveDispatchId is not { } dispatchId) {
                    return;
                }
                GalateaOutboundMailSnapshot? mail = snapshot.Mails
                    .SingleOrDefault(value => string.Equals(
                        value.DispatchId,
                        dispatchId,
                        StringComparison.Ordinal
                    ));
                if (mail?.State is not (
                        GalateaDurableMailState.Started
                        or GalateaDurableMailState.OutcomeUnknown
                        or GalateaDurableMailState.Accepted)) {
                    return;
                }
                DebugUtil.Info(
                    LogCategory,
                    "Durable active dispatch preserved for cold-restart "
                        + $"reconciliation: user={Safe(UserId)}, "
                        + $"dispatchId={dispatchId}, state={mail.State}, "
                        + "preservedForColdRestartReconciliation=true."
                );
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                DebugUtil.Warning(
                    LogCategory,
                    "Durable delegation shutdown state inspection failed: "
                        + $"user={Safe(UserId)}, "
                        + $"exception={exception.GetType().Name}.",
                    exception,
                    DebugEventKind.Failure
                );
            }
        }

        internal void MarkUnavailable(string code) {
            lock (_gate) {
                _availability =
                    GalateaDelegationUserAvailability.Unavailable;
                _unavailableCode = code;
            }
        }

        internal void DisposeStore() {
            lock (_gate) {
                _store?.Dispose();
                _store = null;
                _driver = null;
            }
        }
    }
}
