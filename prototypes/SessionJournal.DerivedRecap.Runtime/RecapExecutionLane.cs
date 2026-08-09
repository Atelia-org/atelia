using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Abstractions;

namespace Atelia.SessionJournal.DerivedRecap.Runtime;

/// <summary>
/// Per-call operational attribution. This context is observability-only and
/// never participates in durable Maintainer capability identity.
/// </summary>
public sealed record RecapCallContext {
    public RecapCallContext(
        string maintainerId,
        ContextHeaderBlockPath target,
        string? sourceId = null
    ) {
        MaintainerId = string.IsNullOrWhiteSpace(maintainerId)
            ? throw new ArgumentException(
                "Maintainer id cannot be empty.",
                nameof(maintainerId)
            )
            : maintainerId;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        SourceId = sourceId;
    }

    public string MaintainerId { get; }

    public ContextHeaderBlockPath Target { get; }

    public string? SourceId { get; }
}

/// <summary>
/// The sole owner of one recap runtime route's client, model, invocation
/// options, and send authority.
/// </summary>
public sealed class RecapExecutionLane {
    public const int DefaultMaxConcurrentCalls = 8;

    private readonly ICompletionClient _rawClient;
    private readonly LoggingCompletionClient? _loggingClient;
    private readonly CompletionInvocationOptions _invocationOptions;
    private readonly string _command;
    private readonly string? _loggingIdentity;
    private readonly PriorityAdmissionGate _admission;

    internal RecapExecutionLane(
        ICompletionClient rawClient,
        string modelId,
        int? maxTokens = null,
        int maxConcurrentCalls = DefaultMaxConcurrentCalls
    ) : this(
        rawClient,
        modelId,
        maxTokens,
        maxConcurrentCalls,
        loggingClient: null,
        "derived-recap/maintenance",
        loggingIdentity: null
    ) { }

    private RecapExecutionLane(
        ICompletionClient rawClient,
        string modelId,
        int? maxTokens,
        int maxConcurrentCalls,
        LoggingCompletionClient? loggingClient,
        string command,
        string? loggingIdentity
    ) {
        _rawClient = rawClient
            ?? throw new ArgumentNullException(nameof(rawClient));
        ModelId = string.IsNullOrWhiteSpace(modelId)
            ? throw new ArgumentException(
                "Model id cannot be empty.",
                nameof(modelId)
            )
            : modelId;
        if (maxTokens is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }
        MaxTokens = maxTokens;
        if (maxConcurrentCalls <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentCalls)
            );
        }
        MaxConcurrentCalls = maxConcurrentCalls;
        _admission = new PriorityAdmissionGate(maxConcurrentCalls);
        _loggingClient = loggingClient;
        _loggingIdentity = loggingIdentity;
        _command = string.IsNullOrWhiteSpace(command)
            ? throw new ArgumentException(
                "Call-log command cannot be empty.",
                nameof(command)
            )
            : command;
        _invocationOptions = new CompletionInvocationOptions {
            PromptCacheReuseHint = PromptCacheReuseHint.NoReuseExpected
        };
    }

    public string ModelId { get; }

    public int? MaxTokens { get; }

    public int MaxConcurrentCalls { get; }

    public PromptCacheReuseHint PromptCacheReuseHint =>
        _invocationOptions.PromptCacheReuseHint;

    public IReadOnlyList<string> WrittenCallLogPaths =>
        _loggingClient?.WrittenCallLogPaths ?? [];

    internal ICompletionClient RawClient => _rawClient;

    internal static RecapExecutionLane CreateWithLogging(
        ICompletionClient rawClient,
        CompletionConnectionConfig connection,
        string callLogDirectory,
        string command,
        int maxConcurrentCalls = DefaultMaxConcurrentCalls
    ) {
        ArgumentNullException.ThrowIfNull(rawClient);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(callLogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var loggingClient = new LoggingCompletionClient(
            rawClient,
            connection,
            callLogDirectory
        );
        return new RecapExecutionLane(
            rawClient,
            connection.ModelId,
            connection.MaxTokens,
            maxConcurrentCalls,
            loggingClient,
            command,
            BuildLoggingIdentity(callLogDirectory, command)
        );
    }

    internal async Task<CompletionResult> SendAsync(
        CompletionPromptPrefix promptPrefix,
        IReadOnlyList<IHistoryMessage> tailMessages,
        RecapCallContext callContext,
        IRecapMaintainerCallControl callControl,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(promptPrefix);
        ArgumentNullException.ThrowIfNull(tailMessages);
        ArgumentNullException.ThrowIfNull(callContext);
        ArgumentNullException.ThrowIfNull(callControl);
        var request = new CompletionRequest(
            ModelId,
            promptPrefix,
            tailMessages,
            MaxTokens
        );
        await callControl.WaitForDispatchPermissionAsync(
                cancellationToken
            )
            .ConfigureAwait(false);
        using PriorityAdmissionGate.Lease lease = await _admission
            .AcquireAsync(callControl, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        callControl.MarkDispatchStarted();
        return await (_loggingClient is null
            ? _rawClient.StreamCompletionAsync(
                request,
                _invocationOptions,
                observer: null,
                cancellationToken
            )
            : _loggingClient.StreamCompletionAsync(
                request,
                _invocationOptions,
                ToLogContext(callContext),
                observer: null,
                cancellationToken
            )).ConfigureAwait(false);
    }

    private CompletionCallLogContext ToLogContext(
        RecapCallContext context
    ) => new(
        Command: _command,
        MaintainerId: context.MaintainerId,
        TargetCarrier: ContextHeaderCarrierTokens.ToStorageToken(
            context.Target.Carrier
        ),
        TargetBlockId: context.Target.BlockKey,
        SourceId: context.SourceId
    );

    internal static string BuildLoggingIdentity(
        string callLogDirectory,
        string command
    ) => Path.GetFullPath(callLogDirectory)
        + "\n"
        + command;

    internal bool HasLoggingIdentity(string? expected) =>
        string.Equals(
            _loggingIdentity,
            expected,
            StringComparison.Ordinal
        );

    private sealed class PriorityAdmissionGate {
        private readonly object _gate = new();
        private readonly int _capacity;
        private readonly LinkedList<Waiter> _leaders = new();
        private readonly LinkedList<Waiter> _followers = new();
        private int _active;

        internal PriorityAdmissionGate(int capacity) {
            _capacity = capacity;
        }

        internal ValueTask<Lease> AcquireAsync(
            IRecapMaintainerCallControl callControl,
            CancellationToken cancellationToken
        ) {
            ArgumentNullException.ThrowIfNull(callControl);
            if (cancellationToken.IsCancellationRequested) {
                return ValueTask.FromCanceled<Lease>(cancellationToken);
            }
            lock (_gate) {
                if (cancellationToken.IsCancellationRequested) {
                    return ValueTask.FromCanceled<Lease>(
                        cancellationToken
                    );
                }
                DiscardCancelledWaiters(_leaders);
                DiscardCancelledWaiters(_followers);
                if (_active < _capacity
                    && _leaders.Count == 0
                    && _followers.Count == 0) {
                    _active++;
                    try {
                        callControl.MarkLaneAdmissionRequested();
                    }
                    catch {
                        _active--;
                        throw;
                    }
                    return ValueTask.FromResult(new Lease(this));
                }
                var waiter = new Waiter(cancellationToken);
                LinkedList<Waiter> waiters =
                    callControl.Role == RecapMaintainerCallRole.Leader
                    ? _leaders
                    : _followers;
                LinkedListNode<Waiter> node = waiters.AddLast(waiter);
                try {
                    callControl.MarkLaneAdmissionRequested();
                }
                catch {
                    waiters.Remove(node);
                    waiter.Abandon();
                    throw;
                }
                return new ValueTask<Lease>(waiter.Task);
            }
        }

        private void Release() {
            while (true) {
                Waiter? waiter;
                lock (_gate) {
                    DiscardCancelledWaiters(_leaders);
                    DiscardCancelledWaiters(_followers);
                    waiter = _leaders.Count > 0
                        ? RemoveFirst(_leaders)
                        : _followers.Count > 0
                            ? RemoveFirst(_followers)
                            : null;
                    if (waiter is null) {
                        _active--;
                        return;
                    }
                }
                if (waiter.TryAdmit(new Lease(this))) {
                    return;
                }
            }
        }

        private static void DiscardCancelledWaiters(
            LinkedList<Waiter> waiters
        ) {
            while (waiters.First is { Value: { } waiter }
                   && waiter.IsCompleted) {
                waiters.RemoveFirst();
                waiter.Abandon();
            }
        }

        private static Waiter RemoveFirst(
            LinkedList<Waiter> waiters
        ) {
            Waiter waiter = waiters.First!.Value;
            waiters.RemoveFirst();
            return waiter;
        }

        internal sealed class Lease : IDisposable {
            private PriorityAdmissionGate? _owner;

            internal Lease(PriorityAdmissionGate owner) {
                _owner = owner;
            }

            public void Dispose() => Interlocked.Exchange(
                    ref _owner,
                    null
                )
                ?.Release();
        }

        private sealed class Waiter {
            private readonly TaskCompletionSource<Lease> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;

            internal Waiter(CancellationToken cancellationToken) {
                _registration = cancellationToken.Register(
                    static state => {
                        var value = ((Waiter Waiter,
                            CancellationToken Token))state!;
                        value.Waiter._completion.TrySetCanceled(value.Token);
                    },
                    (this, cancellationToken)
                );
            }

            internal Task<Lease> Task => _completion.Task;

            internal bool IsCompleted => _completion.Task.IsCompleted;

            internal bool TryAdmit(Lease lease) {
                _registration.Dispose();
                return _completion.TrySetResult(lease);
            }

            internal void Abandon() => _registration.Dispose();
        }
    }
}

/// <summary>
/// Interns lanes by opaque route object identity. Value equality is never a
/// grouping authority.
/// </summary>
public sealed class RecapExecutionLaneInterner {
    private readonly object _gate = new();
    private readonly Dictionary<object, RecapExecutionLane> _byRoute =
        new(ReferenceEqualityComparer.Instance);

    public RecapExecutionLane GetOrAdd(
        object routeAffinity,
        ICompletionClient rawClient,
        string modelId,
        int? maxTokens = null,
        int maxConcurrentCalls =
            RecapExecutionLane.DefaultMaxConcurrentCalls
    ) => GetOrAddCore(
        routeAffinity,
        rawClient,
        modelId,
        maxTokens,
        maxConcurrentCalls,
        loggingIdentity: null,
        factory: () => new RecapExecutionLane(
            rawClient,
            modelId,
            maxTokens,
            maxConcurrentCalls
        )
    );

    public RecapExecutionLane GetOrAddWithLogging(
        object routeAffinity,
        ICompletionClient rawClient,
        CompletionConnectionConfig connection,
        string callLogDirectory,
        string command,
        int maxConcurrentCalls =
            RecapExecutionLane.DefaultMaxConcurrentCalls
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(callLogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return GetOrAddCore(
            routeAffinity,
            rawClient,
            connection.ModelId,
            connection.MaxTokens,
            maxConcurrentCalls,
            RecapExecutionLane.BuildLoggingIdentity(
                callLogDirectory,
                command
            ),
            () => RecapExecutionLane.CreateWithLogging(
                rawClient,
                connection,
                callLogDirectory,
                command,
                maxConcurrentCalls
            )
        );
    }

    private RecapExecutionLane GetOrAddCore(
        object routeAffinity,
        ICompletionClient rawClient,
        string modelId,
        int? maxTokens,
        int maxConcurrentCalls,
        string? loggingIdentity,
        Func<RecapExecutionLane> factory
    ) {
        ArgumentNullException.ThrowIfNull(routeAffinity);
        ArgumentNullException.ThrowIfNull(rawClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (maxTokens is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }
        if (maxConcurrentCalls <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentCalls)
            );
        }
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate) {
            if (_byRoute.TryGetValue(
                    routeAffinity,
                    out RecapExecutionLane? existing
                )) {
                if (!ReferenceEquals(existing.RawClient, rawClient)
                    || !string.Equals(
                        existing.ModelId,
                        modelId,
                        StringComparison.Ordinal
                    )
                    || existing.MaxTokens != maxTokens
                    || existing.MaxConcurrentCalls != maxConcurrentCalls
                    || !existing.HasLoggingIdentity(loggingIdentity)) {
                    throw new InvalidOperationException(
                        "The same recap route affinity was rebound to a different raw client, model, max-token, concurrency, or logging policy."
                    );
                }
                return existing;
            }
            RecapExecutionLane created = factory()
                ?? throw new InvalidOperationException(
                    "Recap execution lane factory returned null."
                );
            _byRoute.Add(routeAffinity, created);
            return created;
        }
    }
}
