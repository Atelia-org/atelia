using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

public static class RecapCompletionProtocolV1 {
    public const string RuntimeProtocolId = "tool-runtime-v1";
    public const string OutputProtocolId = "atelia.recap.output.v1";
    public const string InputProtocolId = "atelia.recap.input.v1";
    public const string PriorProjectionSchemaId = "atelia.recap.prior.v1";
    public const string HistorySegmentRenderingSchemaId = "atelia.history.segment.v1";
    public const string UpdatedOutcome = "updated";
    public const string KeepUnchangedOutcome = "keep-unchanged";
}

public readonly record struct RecapCompletionRouteKey {
    public RecapCompletionRouteKey(
        FamilyDefinitionDigest familyDigest,
        string runtimeProtocolId,
        string? semanticModelId
    ) {
        if (familyDigest.Value is null) {
            throw new ArgumentException(
                "Family digest must not be default.",
                nameof(familyDigest)
            );
        }
        if (string.IsNullOrWhiteSpace(runtimeProtocolId)) {
            throw new ArgumentException(
                "Runtime protocol id must not be empty.",
                nameof(runtimeProtocolId)
            );
        }
        if (semanticModelId is not null
            && string.IsNullOrWhiteSpace(semanticModelId)) {
            throw new ArgumentException(
                "Semantic model id must be null or non-empty.",
                nameof(semanticModelId)
            );
        }
        FamilyDigest = familyDigest;
        RuntimeProtocolId = runtimeProtocolId;
        SemanticModelId = semanticModelId;
    }

    public FamilyDefinitionDigest FamilyDigest { get; }
    public string RuntimeProtocolId { get; }
    public string? SemanticModelId { get; }
}

public interface IRecapCompletionInvoker {
    string ProviderId { get; }
    string ApiSpecId { get; }

    ValueTask<CompletionResult> InvokeAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CancellationToken cancellationToken
    );
}

public enum RecapCompletionResourceOwnership {
    Owned,
    Borrowed
}

public sealed class CompletionClientRecapInvoker : IRecapCompletionInvoker,
    IDisposable, IAsyncDisposable {
    private readonly ICompletionClient _client;
    private readonly RecapCompletionResourceOwnership _clientOwnership;
    private int _disposed;

    public CompletionClientRecapInvoker(
        ICompletionClient client,
        RecapCompletionResourceOwnership clientOwnership
    ) {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (!Enum.IsDefined(clientOwnership)) {
            throw new ArgumentOutOfRangeException(nameof(clientOwnership));
        }
        _clientOwnership = clientOwnership;
    }

    public string ProviderId => _client.Name;
    public string ApiSpecId => _client.ApiSpecId;

    public async ValueTask<CompletionResult> InvokeAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CancellationToken cancellationToken
    ) {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this
        );
        return await _client.StreamCompletionAsync(
            request,
            invocationOptions,
            observer: null,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        if (_clientOwnership is RecapCompletionResourceOwnership.Borrowed) {
            return;
        }
        if (_client is IDisposable disposable) {
            disposable.Dispose();
        }
        else if (_client is IAsyncDisposable asyncDisposable) {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        if (_clientOwnership is RecapCompletionResourceOwnership.Borrowed) {
            return;
        }
        if (_client is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_client is IDisposable disposable) {
            disposable.Dispose();
        }
    }
}

public sealed class RecapCompletionRoute {
    internal RecapCompletionRoute(
        RecapCompletionRouteKey key,
        string modelId,
        IRecapCompletionInvoker invoker,
        RecapCompletionResourceOwnership invokerOwnership,
        int maximumConcurrency,
        TimeSpan dispatchTimeout,
        int? maximumOutputTokens
    ) {
        if (string.IsNullOrWhiteSpace(modelId)) {
            throw new ArgumentException("Model id must not be empty.", nameof(modelId));
        }
        ArgumentNullException.ThrowIfNull(invoker);
        if (!Enum.IsDefined(invokerOwnership)) {
            throw new ArgumentOutOfRangeException(nameof(invokerOwnership));
        }
        if (maximumConcurrency is < 1 or > 1_024) {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }
        if (dispatchTimeout <= TimeSpan.Zero
            || dispatchTimeout > TimeSpan.FromDays(1)) {
            throw new ArgumentOutOfRangeException(nameof(dispatchTimeout));
        }
        if (maximumOutputTokens is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        }
        Key = key;
        ModelId = modelId;
        Invoker = invoker;
        InvokerOwnership = invokerOwnership;
        MaximumConcurrency = maximumConcurrency;
        DispatchTimeout = dispatchTimeout;
        MaximumOutputTokens = maximumOutputTokens;
    }

    public RecapCompletionRouteKey Key { get; }
    public string ModelId { get; }
    public IRecapCompletionInvoker Invoker { get; }
    public RecapCompletionResourceOwnership InvokerOwnership { get; }
    public int MaximumConcurrency { get; }
    public TimeSpan DispatchTimeout { get; }
    public int? MaximumOutputTokens { get; }

    public static RecapCompletionRoute Create(
        RecapCompletionRouteKey key,
        string modelId,
        IRecapCompletionInvoker invoker,
        RecapCompletionResourceOwnership invokerOwnership,
        int maximumConcurrency,
        TimeSpan dispatchTimeout,
        int? maximumOutputTokens = null
    ) => new(
        key,
        modelId,
        invoker,
        invokerOwnership,
        maximumConcurrency,
        dispatchTimeout,
        maximumOutputTokens
    );
}

public abstract record RecapCompletionRouteResolution {
    private RecapCompletionRouteResolution() { }

    public sealed record Bound : RecapCompletionRouteResolution {
        public Bound(RecapCompletionRoute route) {
            Route = route ?? throw new ArgumentNullException(nameof(route));
        }

        public RecapCompletionRoute Route { get; }
    }

    public sealed record Unavailable : RecapCompletionRouteResolution {
        public Unavailable(string code, string detail) {
            Code = RequireText(code, nameof(code));
            Detail = RequireText(detail, nameof(detail));
        }

        public string Code { get; }
        public string Detail { get; }
    }

    public sealed record Invalid : RecapCompletionRouteResolution {
        public Invalid(string code, string detail) {
            Code = RequireText(code, nameof(code));
            Detail = RequireText(detail, nameof(detail));
        }

        public string Code { get; }
        public string Detail { get; }
    }

    private static string RequireText(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "A non-empty value is required.",
                parameterName
            );
        }
        return value;
    }
}

public interface IRecapCompletionRouteResolver {
    RecapCompletionRouteResolution Resolve(RecapCompletionRouteKey key);
}

public sealed record RecapCompletionRuntimeOptions {
    public RecapCompletionRuntimeOptions(
        CompletionInvocationOptions? invocationOptions = null
    ) {
        InvocationOptions = invocationOptions
            ?? CompletionInvocationOptions.Default;
        InvocationOptions.Validate();
    }

    public CompletionInvocationOptions InvocationOptions { get; }
}

public enum RecapCompletionWorkRole {
    Leader,
    Follower
}

public sealed record RecapCompletionTelemetryEvent {
    public RecapCompletionTelemetryEvent(
        string kind,
        RecapCompletionRouteKey routeKey,
        string modelId,
        string providerId,
        string apiSpecId,
        EvaluationKeyDigest evaluationKey,
        FamilyDefinitionDigest familyDigest,
        MaintainerDefinitionDigest definitionDigest,
        string historySegmentDigest,
        bool isFirstRowPrior,
        PriorInputProjectionDigest? priorProjectionDigest,
        RecapCompletionWorkRole role,
        TimeSpan admissionWait,
        TimeSpan laneWait,
        TimeSpan elapsed,
        PromptCacheReuseHint cacheReuseHint,
        bool resultReceived,
        CompletionTerminationKind? termination,
        int providerErrorCount,
        CompletionUsage? usage,
        string providerOutcome,
        string? code,
        string? detail
    ) {
        Kind = RequireText(kind, nameof(kind));
        RouteKey = routeKey;
        ModelId = RequireText(modelId, nameof(modelId));
        ProviderId = RequireText(providerId, nameof(providerId));
        ApiSpecId = RequireText(apiSpecId, nameof(apiSpecId));
        EvaluationKey = evaluationKey;
        FamilyDigest = familyDigest;
        DefinitionDigest = definitionDigest;
        HistorySegmentDigest = RequireText(
            historySegmentDigest,
            nameof(historySegmentDigest)
        );
        IsFirstRowPrior = isFirstRowPrior;
        PriorProjectionDigest = priorProjectionDigest;
        if (isFirstRowPrior == (priorProjectionDigest is not null)) {
            throw new ArgumentException(
                "Prior telemetry must identify exactly one prior-input kind.",
                nameof(priorProjectionDigest)
            );
        }
        if (!Enum.IsDefined(role)) {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        if (!Enum.IsDefined(cacheReuseHint)) {
            throw new ArgumentOutOfRangeException(nameof(cacheReuseHint));
        }
        if (providerErrorCount < 0) {
            throw new ArgumentOutOfRangeException(nameof(providerErrorCount));
        }
        Role = role;
        AdmissionWait = admissionWait;
        LaneWait = laneWait;
        Elapsed = elapsed;
        CacheReuseHint = cacheReuseHint;
        ResultReceived = resultReceived;
        Termination = termination;
        ProviderErrorCount = providerErrorCount;
        Usage = usage;
        ProviderOutcome = RequireText(
            providerOutcome,
            nameof(providerOutcome)
        );
        Code = code;
        Detail = detail is null
            ? null
            : RuntimeDiagnostics.BoundDetail(detail);
    }

    public string Kind { get; }
    public RecapCompletionRouteKey RouteKey { get; }
    public string ModelId { get; }
    public string ProviderId { get; }
    public string ApiSpecId { get; }
    public EvaluationKeyDigest EvaluationKey { get; }
    public FamilyDefinitionDigest FamilyDigest { get; }
    public MaintainerDefinitionDigest DefinitionDigest { get; }
    public string HistorySegmentDigest { get; }
    public bool IsFirstRowPrior { get; }
    public PriorInputProjectionDigest? PriorProjectionDigest { get; }
    public RecapCompletionWorkRole Role { get; }
    public TimeSpan AdmissionWait { get; }
    public TimeSpan LaneWait { get; }
    public TimeSpan Elapsed { get; }
    public PromptCacheReuseHint CacheReuseHint { get; }
    public bool ResultReceived { get; }
    public CompletionTerminationKind? Termination { get; }
    public int ProviderErrorCount { get; }
    public CompletionUsage? Usage { get; }
    public string ProviderOutcome { get; }
    public string? Code { get; }
    public string? Detail { get; }

    private static string RequireText(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "A non-empty value is required.",
                parameterName
            );
        }
        return value;
    }
}

public interface IRecapCompletionTelemetry {
    void Record(RecapCompletionTelemetryEvent value);
}
