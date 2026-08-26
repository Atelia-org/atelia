using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.HistoryTimeline;
using System.Text;

namespace Atelia.SessionJournal.RecapGrid.Hosting;

public sealed record RecapCompletionTelemetrySnapshot(
    IReadOnlyList<RecapCompletionTelemetryEvent> Events,
    long DroppedEventCount,
    int RetainedUtf8Bytes
);

public abstract record RecapGridAgentConnectionLookupResult {
    private RecapGridAgentConnectionLookupResult() { }

    public sealed record Found(CompletionConnectionConfig Connection)
        : RecapGridAgentConnectionLookupResult;
    public sealed record Absent(string ConnectionId)
        : RecapGridAgentConnectionLookupResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridAgentConnectionLookupResult;
}

/// <summary>
/// Provider-free inspection of one exact configured RecapGrid route. A
/// configured result describes runtime policy only; it is not evidence that a
/// provider client was constructed or a call was dispatched.
/// </summary>
public abstract record RecapGridConfiguredRouteInspectionResult {
    private RecapGridConfiguredRouteInspectionResult() { }

    public sealed record Configured(
        string ConnectionId,
        string ModelId,
        int MaximumConcurrency,
        TimeSpan DispatchTimeout,
        int? MaximumOutputTokens
    ) : RecapGridConfiguredRouteInspectionResult;

    public sealed record ExactRouteAbsent
        : RecapGridConfiguredRouteInspectionResult;

    public sealed record ConnectionAbsent(string ConnectionId)
        : RecapGridConfiguredRouteInspectionResult;

    public sealed record Invalid(string Code, string Detail)
        : RecapGridConfiguredRouteInspectionResult;
}

public sealed class BoundedRecapCompletionTelemetry
    : IRecapCompletionTelemetry {
    private const int MaximumEventUtf8Bytes = 32 * 1024;
    private const int MaximumFieldUtf8Bytes = 4 * 1024;
    private const int MaximumProviderDiagnosticCount = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private readonly object _gate = new();
    private readonly int _maximumEvents;
    private readonly int _maximumRetainedUtf8Bytes;
    private State? _state;

    public BoundedRecapCompletionTelemetry(
        int maximumEvents = 1_024,
        int maximumRetainedUtf8Bytes = 4 * 1024 * 1024
    ) {
        if (maximumEvents is < 1 or > 65_536) {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }
        if (maximumRetainedUtf8Bytes is < MaximumEventUtf8Bytes
            or > 64 * 1024 * 1024) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetainedUtf8Bytes)
            );
        }
        _maximumEvents = maximumEvents;
        _maximumRetainedUtf8Bytes = maximumRetainedUtf8Bytes;
    }

    /// <summary>
    /// True only after the first operational event reaches the collector.
    /// Reading an empty snapshot does not allocate the retained evidence queue.
    /// </summary>
    internal bool IsMaterialized {
        get { lock (_gate) { return _state is not null; } }
    }

    public void Record(RecapCompletionTelemetryEvent value) {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate) {
            _state ??= new State();
            if (!TryMeasure(value, out int utf8Bytes)) {
                _state.Dropped++;
                return;
            }
            while (_state.Events.Count == _maximumEvents
                   || _state.RetainedUtf8Bytes + utf8Bytes
                        > _maximumRetainedUtf8Bytes) {
                Entry removed = _state.Events.Dequeue();
                _state.RetainedUtf8Bytes -= removed.Utf8Bytes;
                _state.Dropped++;
            }
            _state.Events.Enqueue(new Entry(value, utf8Bytes));
            _state.RetainedUtf8Bytes += utf8Bytes;
        }
    }

    public RecapCompletionTelemetrySnapshot ReadSnapshot() {
        lock (_gate) {
            if (_state is null) {
                return new RecapCompletionTelemetrySnapshot([], 0, 0);
            }
            return new RecapCompletionTelemetrySnapshot(
                _state.Events.Select(static entry => entry.Value).ToArray(),
                _state.Dropped,
                _state.RetainedUtf8Bytes
            );
        }
    }

    private static bool TryMeasure(
        RecapCompletionTelemetryEvent value,
        out int utf8Bytes
    ) {
        utf8Bytes = 0;
        int total = 0;
        try {
            Add(value.Kind);
            Add(value.RouteKey.FamilyDigest.Value);
            Add(value.RouteKey.RuntimeProtocolId);
            Add(value.RouteKey.SemanticModelId);
            Add(value.ConnectionId);
            Add(value.ModelId);
            Add(value.ProviderId);
            Add(value.ApiSpecId);
            Add(value.EvaluationKey.Value);
            Add(value.FamilyDigest.Value);
            Add(value.DefinitionDigest.Value);
            Add(value.HistorySegmentDigest);
            Add(value.PriorProjectionDigest?.Value);
            Add(value.ProviderOutcome);
            Add(value.Code);
            Add(value.Detail);
            if (value.Usage?.PromptCache.ProviderDiagnostics is { } values) {
                if (values.Count > MaximumProviderDiagnosticCount) {
                    return false;
                }
                foreach ((string key, string item) in values) {
                    Add(key);
                    Add(item);
                }
            }
            utf8Bytes = total;
            return total <= MaximumEventUtf8Bytes;
        }
        catch (EncoderFallbackException) {
            utf8Bytes = 0;
            return false;
        }

        void Add(string? text) {
            if (text is null) { return; }
            int count = StrictUtf8.GetByteCount(text);
            if (count > MaximumFieldUtf8Bytes) {
                throw new EncoderFallbackException(
                    "Operational evidence field exceeds its bound."
                );
            }
            total = checked(total + count);
        }
    }

    private sealed class State {
        internal Queue<Entry> Events { get; } = [];
        internal long Dropped { get; set; }
        internal int RetainedUtf8Bytes { get; set; }
    }

    private sealed record Entry(
        RecapCompletionTelemetryEvent Value,
        int Utf8Bytes
    );
}

public sealed class RecapGridRuntimeHost : IDisposable, IAsyncDisposable {
    private readonly CompletionConnectionRegistry _registry;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    private RecapGridRuntimeHost(
        CompletionConnectionRegistry registry,
        RecapCompletionRuntime runtime,
        BoundedRecapCompletionTelemetry telemetry
    ) {
        _registry = registry;
        Runtime = runtime;
        Telemetry = telemetry;
    }

    public RecapCompletionRuntime Runtime { get; }
    public IRecapCellBatchExecutor Executor => Runtime;
    internal BoundedRecapCompletionTelemetry Telemetry { get; }

    public RecapCompletionTelemetrySnapshot ReadTelemetrySnapshot()
        => Telemetry.ReadSnapshot();

    public static RecapGridRuntimeHost Create(
        RecapGridRouteManifest manifest,
        CompletionConnectionsFileConfig connections,
        ICompletionClientFactory clientFactory,
        RecapCompletionRuntimeOptions? runtimeOptions = null,
        int maximumTelemetryEvents = 1_024
    ) {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clientFactory);
        CompletionConnectionsFileConfig frozenConnections =
            RecapGridCompletionConnectionsManifest.Freeze(connections);
        var registry = new CompletionConnectionRegistry(
            frozenConnections,
            clientFactory
        );
        try {
            var telemetry = new BoundedRecapCompletionTelemetry(
                maximumTelemetryEvents
            );
            var resolver = new HostingExactRouteResolver(
                manifest,
                registry
            );
            var runtime = new RecapCompletionRuntime(
                resolver,
                runtimeOptions,
                telemetry
            );
            return new RecapGridRuntimeHost(registry, runtime, telemetry);
        }
        catch {
            registry.Dispose();
            throw;
        }
    }

    public void Dispose() => BeginDispose().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(BeginDispose());

    private Task BeginDispose() {
        lock (_disposeGate) {
            return _disposeTask ??= DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync() {
        try {
            await Runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally {
            await _registry.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed class HostingExactRouteResolver
        : IRecapCompletionRouteResolver {
        private readonly IReadOnlyDictionary<RecapCompletionRouteKey,
            RecapGridRouteManifestEntry> _routes;
        private readonly CompletionConnectionRegistry _registry;

        internal HostingExactRouteResolver(
            RecapGridRouteManifest manifest,
            CompletionConnectionRegistry registry
        ) {
            _routes = manifest.Routes.ToDictionary(static route => route.Key);
            _registry = registry;
        }

        public RecapCompletionRouteResolution Resolve(
            RecapCompletionRouteKey key
        ) {
            if (!_routes.TryGetValue(key, out var route)) {
                return new RecapCompletionRouteResolution.Unavailable(
                    "ExactRouteAbsent",
                    "No exact recap completion route is configured."
                );
            }
            if (!_registry.TryGet(route.ConnectionId, out var connection)) {
                return new RecapCompletionRouteResolution.Unavailable(
                    "RouteConnectionAbsent",
                    "The exact route connection is not configured."
                );
            }
            try {
                var invoker = new CompletionClientRecapInvoker(
                    _registry.GetClient(connection.Id),
                    RecapCompletionResourceOwnership.Borrowed
                );
                return new RecapCompletionRouteResolution.Bound(
                    RecapCompletionRoute.Create(
                        key,
                        route.ConnectionId,
                        connection.ModelId,
                        invoker,
                        RecapCompletionResourceOwnership.Owned,
                        route.MaximumConcurrency,
                        route.DispatchTimeout,
                        route.MaximumOutputTokens
                    )
                );
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException
                    and not StackOverflowException
                    and not AccessViolationException) {
                return new RecapCompletionRouteResolution.Invalid(
                    "RouteClientConstructionFailed",
                    exception.GetType().Name
                );
            }
        }
    }
}

public abstract record RecapGridAgentConnectionResult {
    private RecapGridAgentConnectionResult() { }

    public sealed record Bound(
        CompletionConnectionConfig Connection,
        ICompletionClient Client,
        CompletionDispatchIdentity Identity
    ) : RecapGridAgentConnectionResult;
    public sealed record Absent(string ConnectionId)
        : RecapGridAgentConnectionResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridAgentConnectionResult;
}

/// <summary>
/// Candidate-host completion boundary. One strict connection registry serves
/// both the main agent and lazy RecapGrid routes. A host created with
/// <see cref="Create"/> owns that registry; a host created with
/// <see cref="CreateBorrowingRegistry(Func{RecapGridRouteManifest},CompletionConnectionRegistry,RecapCompletionRuntimeOptions,int)"/>
/// borrows it. Runtime disposal always drains before an owned registry releases
/// its distinct clients.
/// </summary>
public sealed class RecapGridCompletionHost : IDisposable, IAsyncDisposable {
    private readonly CompletionConnectionRegistry _registry;
    private readonly DeferredSharedRegistryRouteResolver _routeResolver;
    private readonly RecapGridAgentControlProfileRegistry? _agentControl;
    private readonly bool _ownsRegistry;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    private RecapGridCompletionHost(
        CompletionConnectionRegistry registry,
        DeferredSharedRegistryRouteResolver routeResolver,
        RecapCompletionRuntime runtime,
        BoundedRecapCompletionTelemetry telemetry,
        RecapGridAgentControlProfileRegistry? agentControl,
        bool ownsRegistry
    ) {
        _registry = registry;
        _routeResolver = routeResolver;
        Runtime = runtime;
        Telemetry = telemetry;
        _agentControl = agentControl;
        _ownsRegistry = ownsRegistry;
    }

    public RecapCompletionRuntime Runtime { get; }
    public IRecapCellBatchExecutor Executor => Runtime;
    internal BoundedRecapCompletionTelemetry Telemetry { get; }

    public RecapCompletionTelemetrySnapshot ReadTelemetrySnapshot()
        => Telemetry.ReadSnapshot();

    public static RecapGridCompletionHost Create(
        Func<RecapGridRouteManifest> routeManifestLoader,
        CompletionConnectionsFileConfig connections,
        ICompletionClientFactory clientFactory,
        RecapCompletionRuntimeOptions? runtimeOptions = null,
        int maximumTelemetryEvents = 1_024
    ) => CreateCore(
        routeManifestLoader,
        connections,
        clientFactory,
        agentControl: null,
        runtimeOptions,
        maximumTelemetryEvents
    );

    public static RecapGridCompletionHost Create(
        Func<RecapGridRouteManifest> routeManifestLoader,
        CompletionConnectionsFileConfig connections,
        ICompletionClientFactory clientFactory,
        RecapGridAgentControlProfileRegistry agentControl,
        RecapCompletionRuntimeOptions? runtimeOptions = null,
        int maximumTelemetryEvents = 1_024
    ) {
        ArgumentNullException.ThrowIfNull(agentControl);
        return CreateCore(
            routeManifestLoader,
            connections,
            clientFactory,
            agentControl,
            runtimeOptions,
            maximumTelemetryEvents
        );
    }

    /// <summary>
    /// Creates a host that borrows one caller-owned connection registry while
    /// owning its RecapGrid runtime, route resolver, and telemetry. The caller
    /// must supply a registry created from already normalized and frozen
    /// connection configuration, keep it alive until this host is disposed,
    /// and dispose it after this host has drained. Disposing this host never
    /// disposes the borrowed registry or any client owned by it.
    /// </summary>
    public static RecapGridCompletionHost CreateBorrowingRegistry(
        Func<RecapGridRouteManifest> routeManifestLoader,
        CompletionConnectionRegistry registry,
        RecapCompletionRuntimeOptions? runtimeOptions = null,
        int maximumTelemetryEvents = 1_024
    ) => CreateWithRegistry(
        routeManifestLoader,
        registry,
        agentControl: null,
        runtimeOptions,
        maximumTelemetryEvents,
        ownsRegistry: false
    );

    /// <summary>
    /// Creates a host that borrows one caller-owned connection registry while
    /// owning its RecapGrid runtime, route resolver, telemetry, and exact Agent
    /// Control profile lookup. The caller must supply a registry created from
    /// already normalized and frozen connection configuration, keep it alive
    /// until this host is disposed, and dispose it after this host has drained.
    /// Disposing this host never disposes the borrowed registry or any client
    /// owned by it.
    /// </summary>
    public static RecapGridCompletionHost CreateBorrowingRegistry(
        Func<RecapGridRouteManifest> routeManifestLoader,
        CompletionConnectionRegistry registry,
        RecapGridAgentControlProfileRegistry agentControl,
        RecapCompletionRuntimeOptions? runtimeOptions = null,
        int maximumTelemetryEvents = 1_024
    ) {
        ArgumentNullException.ThrowIfNull(agentControl);
        return CreateWithRegistry(
            routeManifestLoader,
            registry,
            agentControl,
            runtimeOptions,
            maximumTelemetryEvents,
            ownsRegistry: false
        );
    }

    private static RecapGridCompletionHost CreateCore(
        Func<RecapGridRouteManifest> routeManifestLoader,
        CompletionConnectionsFileConfig connections,
        ICompletionClientFactory clientFactory,
        RecapGridAgentControlProfileRegistry? agentControl,
        RecapCompletionRuntimeOptions? runtimeOptions,
        int maximumTelemetryEvents
    ) {
        ArgumentNullException.ThrowIfNull(routeManifestLoader);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clientFactory);
        CompletionConnectionsFileConfig frozen =
            RecapGridCompletionConnectionsManifest.Freeze(connections);
        var registry = new CompletionConnectionRegistry(frozen, clientFactory);
        try {
            return CreateWithRegistry(
                routeManifestLoader,
                registry,
                agentControl,
                runtimeOptions,
                maximumTelemetryEvents,
                ownsRegistry: true
            );
        }
        catch {
            registry.Dispose();
            throw;
        }
    }

    private static RecapGridCompletionHost CreateWithRegistry(
        Func<RecapGridRouteManifest> routeManifestLoader,
        CompletionConnectionRegistry registry,
        RecapGridAgentControlProfileRegistry? agentControl,
        RecapCompletionRuntimeOptions? runtimeOptions,
        int maximumTelemetryEvents,
        bool ownsRegistry
    ) {
        ArgumentNullException.ThrowIfNull(routeManifestLoader);
        ArgumentNullException.ThrowIfNull(registry);
        var telemetry = new BoundedRecapCompletionTelemetry(
            maximumTelemetryEvents);
        var resolver = new DeferredSharedRegistryRouteResolver(
            routeManifestLoader,
            registry);
        var runtime = new RecapCompletionRuntime(
            resolver, runtimeOptions, telemetry);
        return new RecapGridCompletionHost(
            registry,
            resolver,
            runtime,
            telemetry,
            agentControl,
            ownsRegistry
        );
    }

    public RecapGridAgentControlOpenResult OpenAgentControl(
        SessionJournalReadView selectedRef,
        string profileId,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        if (_agentControl is null
            || !_agentControl.TryGet(profileId, out var profile)) {
            return new RecapGridAgentControlOpenResult.ProfileAbsent(
                profileId
            );
        }
        return RecapGridAgentControlFactory.Bind(
            selectedRef,
            profile,
            estimators
        );
    }

    public RecapGridAgentControlOpenResult BindAgentControlExact(
        SessionJournalReadView selectedRef,
        SessionToolRuntimeIdentity runtimeIdentity,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(runtimeIdentity);
        if (_agentControl is null
            || !_agentControl.TryBindExact(runtimeIdentity, out var profile)) {
            return new RecapGridAgentControlOpenResult.ProfileAbsent(
                runtimeIdentity.CapabilitySetFingerprint
            );
        }
        return RecapGridAgentControlFactory.Bind(
            selectedRef,
            profile,
            estimators
        );
    }

    public RecapGridAgentConnectionResult BindAgentExact(
        string connectionId
    ) {
        if (string.IsNullOrWhiteSpace(connectionId)) {
            return new RecapGridAgentConnectionResult.Invalid(
                "AgentConnectionIdInvalid",
                "An exact non-empty agent connection ID is required.");
        }
        if (!_registry.TryGet(connectionId, out var connection)) {
            return new RecapGridAgentConnectionResult.Absent(connectionId);
        }
        try {
            ICompletionClient client = _registry.GetClient(connection.Id);
            return new RecapGridAgentConnectionResult.Bound(
                connection,
                client,
                CompletionDispatchIdentityFactory.Create(connection, client));
        }
        catch (Exception exception) when (IsNonFatal(exception)) {
            return new RecapGridAgentConnectionResult.Invalid(
                "AgentClientConstructionFailed",
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Resolves one exact configured connection without constructing its
    /// provider client. Candidate hosts use this for setup/admission preflight
    /// before the first provider-side resource exists.
    /// </summary>
    public RecapGridAgentConnectionLookupResult InspectAgentExact(
        string connectionId
    ) {
        if (string.IsNullOrWhiteSpace(connectionId)) {
            return new RecapGridAgentConnectionLookupResult.Invalid(
                "AgentConnectionIdInvalid",
                "An exact non-empty agent connection ID is required.");
        }
        return _registry.TryGet(connectionId, out var connection)
            ? new RecapGridAgentConnectionLookupResult.Found(connection)
            : new RecapGridAgentConnectionLookupResult.Absent(connectionId);
    }

    /// <summary>
    /// Inspects one exact deferred route and its frozen connection config
    /// without constructing a provider client.
    /// </summary>
    public RecapGridConfiguredRouteInspectionResult InspectRouteExact(
        RecapCompletionRouteKey key
    ) => _routeResolver.Inspect(key);

    public CompletionDispatchBindingResult BindPreparedExact(
        CompletionDispatchIdentity required
    ) => _registry.BindExact(required);

    public void Dispose() => BeginDispose().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(BeginDispose());

    private Task BeginDispose() {
        lock (_disposeGate) {
            return _disposeTask ??= DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync() {
        try {
            await Runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally {
            if (_ownsRegistry) {
                await _registry.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsNonFatal(Exception exception)
        => exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private sealed class DeferredSharedRegistryRouteResolver
        : IRecapCompletionRouteResolver {
        private readonly Lazy<IReadOnlyDictionary<RecapCompletionRouteKey,
            RecapGridRouteManifestEntry>> _routes;
        private readonly CompletionConnectionRegistry _registry;

        internal DeferredSharedRegistryRouteResolver(
            Func<RecapGridRouteManifest> loader,
            CompletionConnectionRegistry registry
        ) {
            _registry = registry;
            _routes = new Lazy<IReadOnlyDictionary<
                RecapCompletionRouteKey, RecapGridRouteManifestEntry>>(
                () => loader().Routes.ToDictionary(static route => route.Key),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public RecapCompletionRouteResolution Resolve(
            RecapCompletionRouteKey key
        ) {
            RecapGridConfiguredRouteInspectionResult inspected = InspectCore(
                key,
                out RecapGridRouteManifestEntry? route,
                out CompletionConnectionConfig? connection
            );
            if (inspected is RecapGridConfiguredRouteInspectionResult
                    .ExactRouteAbsent) {
                return new RecapCompletionRouteResolution.Unavailable(
                    "ExactRouteAbsent",
                    "No exact recap completion route is configured.");
            }
            if (inspected is RecapGridConfiguredRouteInspectionResult
                    .ConnectionAbsent) {
                return new RecapCompletionRouteResolution.Unavailable(
                    "RouteConnectionAbsent",
                    "The exact route connection is not configured.");
            }
            if (inspected is RecapGridConfiguredRouteInspectionResult.Invalid
                    invalid) {
                return new RecapCompletionRouteResolution.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            }
            if (inspected is not RecapGridConfiguredRouteInspectionResult
                    .Configured || route is null || connection is null) {
                return new RecapCompletionRouteResolution.Invalid(
                    "RouteInspectionInvalid",
                    "The configured route inspection returned an unknown outcome."
                );
            }
            try {
                var invoker = new CompletionClientRecapInvoker(
                    _registry.GetClient(connection.Id),
                    RecapCompletionResourceOwnership.Borrowed);
                return new RecapCompletionRouteResolution.Bound(
                    RecapCompletionRoute.Create(
                        key,
                        route.ConnectionId,
                        connection.ModelId,
                        invoker,
                        RecapCompletionResourceOwnership.Owned,
                        route.MaximumConcurrency,
                        route.DispatchTimeout,
                        route.MaximumOutputTokens));
            }
            catch (Exception exception) when (IsNonFatal(exception)) {
                return new RecapCompletionRouteResolution.Invalid(
                    "RouteClientConstructionFailed",
                    exception.GetType().Name);
            }
        }

        internal RecapGridConfiguredRouteInspectionResult Inspect(
            RecapCompletionRouteKey key
        ) => InspectCore(key, out _, out _);

        private RecapGridConfiguredRouteInspectionResult InspectCore(
            RecapCompletionRouteKey key,
            out RecapGridRouteManifestEntry? route,
            out CompletionConnectionConfig? connection
        ) {
            route = null;
            connection = null;
            IReadOnlyDictionary<RecapCompletionRouteKey,
                RecapGridRouteManifestEntry> routes;
            try {
                routes = _routes.Value;
            }
            catch (Exception exception) when (IsNonFatal(exception)) {
                return new RecapGridConfiguredRouteInspectionResult.Invalid(
                    "RouteManifestLoadFailed", exception.GetType().Name);
            }
            if (!routes.TryGetValue(key, out route)) {
                return new RecapGridConfiguredRouteInspectionResult
                    .ExactRouteAbsent();
            }
            if (!_registry.TryGet(route.ConnectionId, out connection)) {
                return new RecapGridConfiguredRouteInspectionResult
                    .ConnectionAbsent(route.ConnectionId);
            }
            return new RecapGridConfiguredRouteInspectionResult.Configured(
                route.ConnectionId,
                connection.ModelId,
                route.MaximumConcurrency,
                route.DispatchTimeout,
                route.MaximumOutputTokens
            );
        }
    }
}
