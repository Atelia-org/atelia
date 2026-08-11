using Atelia.Completion;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using System.Text;

namespace Atelia.SessionJournal.RecapGrid.Hosting;

public sealed record RecapCompletionTelemetrySnapshot(
    IReadOnlyList<RecapCompletionTelemetryEvent> Events,
    long DroppedEventCount,
    int RetainedUtf8Bytes
);

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
    public bool IsMaterialized {
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
    public BoundedRecapCompletionTelemetry Telemetry { get; }

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
