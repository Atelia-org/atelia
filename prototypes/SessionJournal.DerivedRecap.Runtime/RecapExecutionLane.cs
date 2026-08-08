using Atelia.Completion;
using Atelia.Completion.Abstractions;

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
    private readonly ICompletionClient _rawClient;
    private readonly LoggingCompletionClient? _loggingClient;
    private readonly CompletionInvocationOptions _invocationOptions;
    private readonly string _command;
    private readonly string? _loggingIdentity;

    internal RecapExecutionLane(
        ICompletionClient rawClient,
        string modelId,
        int? maxTokens = null
    ) : this(
        rawClient,
        modelId,
        maxTokens,
        loggingClient: null,
        "derived-recap/maintenance",
        loggingIdentity: null
    ) { }

    private RecapExecutionLane(
        ICompletionClient rawClient,
        string modelId,
        int? maxTokens,
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

    public PromptCacheReuseHint PromptCacheReuseHint =>
        _invocationOptions.PromptCacheReuseHint;

    public IReadOnlyList<string> WrittenCallLogPaths =>
        _loggingClient?.WrittenCallLogPaths ?? [];

    internal ICompletionClient RawClient => _rawClient;

    internal static RecapExecutionLane CreateWithLogging(
        ICompletionClient rawClient,
        CompletionConnectionConfig connection,
        string callLogDirectory,
        string command
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
            loggingClient,
            command,
            BuildLoggingIdentity(callLogDirectory, command)
        );
    }

    internal Task<CompletionResult> SendAsync(
        CompletionPromptPrefix promptPrefix,
        IReadOnlyList<IHistoryMessage> tailMessages,
        RecapCallContext callContext,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(promptPrefix);
        ArgumentNullException.ThrowIfNull(tailMessages);
        ArgumentNullException.ThrowIfNull(callContext);
        var request = new CompletionRequest(
            ModelId,
            promptPrefix,
            tailMessages,
            MaxTokens
        );
        return _loggingClient is null
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
            );
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
        int? maxTokens = null
    ) => GetOrAddCore(
        routeAffinity,
        rawClient,
        modelId,
        maxTokens,
        loggingIdentity: null,
        factory: () => new RecapExecutionLane(
            rawClient,
            modelId,
            maxTokens
        )
    );

    public RecapExecutionLane GetOrAddWithLogging(
        object routeAffinity,
        ICompletionClient rawClient,
        CompletionConnectionConfig connection,
        string callLogDirectory,
        string command
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(callLogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return GetOrAddCore(
            routeAffinity,
            rawClient,
            connection.ModelId,
            connection.MaxTokens,
            RecapExecutionLane.BuildLoggingIdentity(
                callLogDirectory,
                command
            ),
            () => RecapExecutionLane.CreateWithLogging(
                rawClient,
                connection,
                callLogDirectory,
                command
            )
        );
    }

    private RecapExecutionLane GetOrAddCore(
        object routeAffinity,
        ICompletionClient rawClient,
        string modelId,
        int? maxTokens,
        string? loggingIdentity,
        Func<RecapExecutionLane> factory
    ) {
        ArgumentNullException.ThrowIfNull(routeAffinity);
        ArgumentNullException.ThrowIfNull(rawClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (maxTokens is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
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
                    || !existing.HasLoggingIdentity(loggingIdentity)) {
                    throw new InvalidOperationException(
                        "The same recap route affinity was rebound to a different raw client, model, max-token, or logging policy."
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
