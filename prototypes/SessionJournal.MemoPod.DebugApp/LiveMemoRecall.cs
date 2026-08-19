using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.MemoPod;
using MemoPodAggregate = Atelia.SessionJournal.MemoPod.MemoPod;

namespace Atelia.SessionJournal.MemoPod.DebugApp;

internal static class LiveMemoRecallRunner {
    private static readonly IReadOnlySet<string> SingleKeys = Set(
        "root",
        "pod",
        "live",
        "connections",
        "connection",
        "case",
        "max-prompt-bytes",
        "max-tokens",
        "delay-ms"
    );
    private static readonly IReadOnlySet<string> RepeatedKeys = Set(
        "query-file"
    );

    internal static async Task<int> RunAsync(
        OperatorArguments arguments,
        TextWriter output,
        LiveMemoRecallServices? services,
        CancellationToken cancellationToken
    ) {
        arguments.RequireShape(SingleKeys, RepeatedKeys);
        IReadOnlyList<string> queryPaths = arguments.GetRepeated(
            "query-file"
        );
        if (queryPaths.Count is < 1 or > 8) {
            throw new OperatorSyntaxException();
        }

        string caseLabel = RequireCaseLabel(
            arguments.RequireSingle("case")
        );
        int maximumPromptUtf8Bytes = ParseBoundedInt(
            arguments.GetSingleOrDefault("max-prompt-bytes"),
            MemoPodLimits.MaximumRenderedPromptUtf8Bytes,
            1,
            MemoPodLimits.MaximumRenderedPromptUtf8Bytes
        );
        int maxTokens = ParseBoundedInt(
            arguments.GetSingleOrDefault("max-tokens"),
            defaultValue: 256,
            minimum: 1,
            maximum: MemoPodLimits.MaximumRecallMaxTokens
        );
        int delayMilliseconds = ParseBoundedInt(
            arguments.GetSingleOrDefault("delay-ms"),
            defaultValue: 0,
            minimum: 0,
            maximum: 30_000
        );
        string[] queries = queryPaths.Select(path =>
            StrictUtf8File.Read(
                path,
                MemoPodLimits.MaximumRecallQueryUtf8Bytes
            )
        ).ToArray();

        string root = arguments.RequireSingle("root");
        MemoPodId podId = MemoPodId.Parse(
            arguments.RequireSingle("pod")
        );
        string connectionsPath = arguments.RequireSingle("connections");
        string connectionId = arguments.RequireSingle("connection");
        var options = new MemoRecallOptions(
            MemoPodLimits.MaximumRecallResultCount,
            maxTokens,
            maximumPromptUtf8Bytes,
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes
        );

        services ??= LiveMemoRecallServices.CreateProduction();
        services.EnsureSafety();

        CompletionConnectionsFileConfig connections;
        try {
            connections = services.LoadConnections(connectionsPath);
        }
        catch (Exception exception) when (!IsFatal(exception)
            && exception is not OperationCanceledException) {
            throw new LiveMemoRecallConfigurationException();
        }

        ICompletionClientFactory clientFactory;
        try {
            clientFactory = services.CreateClientFactory()
                ?? throw new LiveMemoRecallConfigurationException();
        }
        catch (LiveMemoRecallConfigurationException) {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            throw new LiveMemoRecallConfigurationException();
        }

        await using var registry = new CompletionConnectionRegistry(
            connections,
            clientFactory
        );
        if (!registry.TryGet(
                connectionId,
                out CompletionConnectionConfig? connection
            )) {
            throw new LiveMemoRecallConfigurationException();
        }
        LiveMemoRecallRoutePolicy.Validate(connection);

        MemoPodAggregate pod = MemoPodAggregate.Open(root, podId);
        int activeMemoCount = pod.List().Count();

        ICompletionClient innerClient;
        try {
            innerClient = registry.GetClient(connection.Id);
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            throw new LiveMemoRecallConfigurationException();
        }
        var client = new ContentFreeRecallCapturingClient(innerClient);

        for (int index = 0; index < queries.Length; index++) {
            if (index > 0 && delayMilliseconds > 0) {
                await Task.Delay(
                    delayMilliseconds,
                    cancellationToken
                ).ConfigureAwait(false);
            }

            string query = queries[index];
            int queryUtf8Bytes = Encoding.UTF8.GetByteCount(query);
            MemoRecallResult? result = null;
            string[]? selectedIds = null;
            string outcome = "failed";
            var stopwatch = Stopwatch.StartNew();
            try {
                result = await pod.RecallAsync(
                    client,
                    connection.ModelId,
                    query,
                    options,
                    cancellationToken
                ).ConfigureAwait(false);
                ContentFreePromptCapture capture = client.LatestCapture
                    ?? throw new InvalidOperationException(
                        "Live MemoPod recall did not capture its prompt."
                    );
                if (client.InvocationCount != index + 1
                    || !string.Equals(
                        capture.Sha256,
                        result.FrozenPromptSha256,
                        StringComparison.Ordinal
                    )) {
                    throw new InvalidOperationException(
                        "Live MemoPod recall prompt identity did not match."
                    );
                }
                selectedIds = result.Memos
                    .Select(static memo => memo.Id.Value)
                    .ToArray();
                outcome = "completed";
            }
            catch (OperationCanceledException) {
                outcome = "cancelled";
                throw;
            }
            finally {
                stopwatch.Stop();
                ContentFreePromptCapture? capture = client.LatestCapture;
                if (capture is not null
                    && client.InvocationCount == index + 1) {
                    ContentFreeUsageCapture usage =
                        client.LatestUsage
                        ?? ContentFreeUsageCapture.Unknown;
                    var evidence = new LiveMemoRecallEvidence(
                        LiveMemoRecallEvidenceSerializer.Schema,
                        caseLabel,
                        index + 1,
                        connection.Id,
                        connection.Kind,
                        connection.ModelId,
                        connection.CompletionSurfaceId,
                        client.Name,
                        client.ApiSpecId,
                        pod.PodId.Value,
                        activeMemoCount,
                        capture.Sha256,
                        capture.Utf8Bytes,
                        queryUtf8Bytes,
                        options.MaxResults,
                        options.MaximumFrozenPromptUtf8Bytes,
                        options.MaxTokens,
                        delayMilliseconds,
                        stopwatch.ElapsedMilliseconds,
                        outcome,
                        LiveMemoRecallEvidenceSerializer.Map(
                            usage.RequestStatus
                        ),
                        LiveMemoRecallEvidenceSerializer.Map(
                            usage.SupportStatus
                        ),
                        LiveMemoRecallEvidenceSerializer.Map(
                            usage.ObservationStatus
                        ),
                        usage.UncachedInputTokens,
                        usage.CacheCreationInputTokens,
                        usage.CacheReadInputTokens,
                        usage.OutputTokens,
                        selectedIds?.Length,
                        selectedIds
                    );
                    await output.WriteLineAsync(
                        LiveMemoRecallEvidenceSerializer.Serialize(evidence)
                    ).ConfigureAwait(false);
                }
            }
        }
        return 0;
    }

    private static string RequireCaseLabel(string value) {
        if (value.Length is < 1 or > 64
            || !IsCaseStart(value[0])
            || value.Any(static character =>
                !IsCaseStart(character)
                && character is not '.' and not '_' and not '-')) {
            throw new OperatorSyntaxException();
        }
        return value;

        static bool IsCaseStart(char character)
            => character is >= 'a' and <= 'z'
                or >= '0' and <= '9';
    }

    private static int ParseBoundedInt(
        string? raw,
        int defaultValue,
        int minimum,
        int maximum
    ) {
        if (raw is null) {
            return defaultValue;
        }
        if (!int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int value
            )
            || value < minimum
            || value > maximum) {
            throw new OperatorSyntaxException();
        }
        return value;
    }

    private static IReadOnlySet<string> Set(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException
            or CannotUnloadAppDomainException;
}

internal static class LiveMemoRecallRoutePolicy {
    internal const string RequiredKind = "openai-chat";
    internal const string RequiredModelId = "deepseek-v4-flash";
    internal const string RequiredCompletionSurfaceId =
        "openai-chat/deepseek-v4";
    internal const string RequiredOriginHost = "api.deepseek.com";

    internal static void Validate(CompletionConnectionConfig connection) {
        ArgumentNullException.ThrowIfNull(connection);

        if (!string.Equals(
                connection.Kind,
                RequiredKind,
                StringComparison.Ordinal
            )
            || !string.Equals(
                connection.ModelId,
                RequiredModelId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                connection.CompletionSurfaceId,
                RequiredCompletionSurfaceId,
                StringComparison.Ordinal
            )
            || connection.ReasoningEffort
                is not CompletionReasoningEffort.Disabled) {
            throw new LiveMemoRecallConfigurationException();
        }

        if (string.IsNullOrWhiteSpace(connection.ApiKeyEnv)
            || string.IsNullOrWhiteSpace(connection.ApiKey)) {
            throw new LiveMemoRecallConfigurationException();
        }

        if (!Uri.TryCreate(
                connection.BaseAddress,
                UriKind.Absolute,
                out Uri? endpoint
            )
            || !string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal
            )
            || !string.Equals(
                endpoint.Host,
                RequiredOriginHost,
                StringComparison.OrdinalIgnoreCase
            )
            || !endpoint.IsDefaultPort
            || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0
            || !string.Equals(
                endpoint.AbsolutePath,
                "/",
                StringComparison.Ordinal
            )) {
            throw new LiveMemoRecallConfigurationException();
        }
    }
}

internal static class LiveMemoRecallSafetyGate {
    internal static void EnsureEnabled() {
#if DEBUG
        throw new LiveMemoRecallSafetyException();
#else
        if (!IsErrorOnly("ATELIA_DEBUG_FILE_LEVEL")
            || !IsErrorOnly("ATELIA_DEBUG_CONSOLE_LEVEL")) {
            throw new LiveMemoRecallSafetyException();
        }
#endif
    }

    private static bool IsErrorOnly(string variableName)
        => string.Equals(
            Environment.GetEnvironmentVariable(variableName)?.Trim(),
            "ERROR",
            StringComparison.OrdinalIgnoreCase
        );
}

internal sealed record LiveMemoRecallServices(
    Action EnsureSafety,
    Func<string, CompletionConnectionsFileConfig> LoadConnections,
    Func<ICompletionClientFactory> CreateClientFactory
) {
    internal static LiveMemoRecallServices CreateProduction()
        => new(
            LiveMemoRecallSafetyGate.EnsureEnabled,
            CompletionConnectionConfigLoader.LoadFile,
            static () => new DefaultCompletionClientFactory()
        );
}

internal sealed class ContentFreeRecallCapturingClient : ICompletionClient {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly ICompletionClient _inner;

    internal ContentFreeRecallCapturingClient(ICompletionClient inner) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Name => _inner.Name;
    public string ApiSpecId => _inner.ApiSpecId;

    internal int InvocationCount { get; private set; }
    internal ContentFreePromptCapture? LatestCapture { get; private set; }
    internal ContentFreeUsageCapture? LatestUsage { get; private set; }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => throw new InvalidOperationException(
        "Live MemoPod recall must use the invocation-options overload."
    );

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocationOptions);

        LatestCapture = CapturePrompt(request);
        LatestUsage = null;
        InvocationCount++;
        CompletionResult result = await _inner.StreamCompletionAsync(
            request,
            invocationOptions,
            observer,
            cancellationToken
        ).ConfigureAwait(false);
        LatestUsage = ContentFreeUsageCapture.From(result.Usage);
        return result;
    }

    private static ContentFreePromptCapture CapturePrompt(
        CompletionRequest request
    ) {
        if (request.PromptPrefix.SharedContextMessages
                is not [ObservationMessage observation]
            || observation.Content is null) {
            throw new InvalidOperationException(
                "Live MemoPod recall requires one shared Observation."
            );
        }

        int byteCount = StrictUtf8.GetByteCount(observation.Content);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(byteCount);
        try {
            int written = StrictUtf8.GetBytes(
                observation.Content.AsSpan(),
                bytes
            );
            if (written != byteCount) {
                throw new InvalidOperationException(
                    "Live MemoPod prompt UTF-8 pre-count did not match."
                );
            }
            string sha256 = Convert.ToHexStringLower(
                SHA256.HashData(bytes)
            );
            return new ContentFreePromptCapture(byteCount, sha256);
        }
        finally {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

internal sealed record ContentFreePromptCapture(
    int Utf8Bytes,
    string Sha256
);

internal sealed record ContentFreeUsageCapture(
    PromptCacheRequestStatus RequestStatus,
    PromptCacheSupportStatus SupportStatus,
    PromptCacheObservationStatus ObservationStatus,
    long? UncachedInputTokens,
    long? CacheCreationInputTokens,
    long? CacheReadInputTokens,
    long? OutputTokens
) {
    internal static ContentFreeUsageCapture Unknown { get; } = new(
        PromptCacheRequestStatus.Unknown,
        PromptCacheSupportStatus.Unknown,
        PromptCacheObservationStatus.Unknown,
        null,
        null,
        null,
        null
    );

    internal static ContentFreeUsageCapture From(CompletionUsage usage) {
        ArgumentNullException.ThrowIfNull(usage);
        return new ContentFreeUsageCapture(
            usage.PromptCache.RequestStatus,
            usage.PromptCache.SupportStatus,
            usage.PromptCache.ObservationStatus,
            usage.UncachedInputTokens,
            usage.CacheCreationInputTokens,
            usage.CacheReadInputTokens,
            usage.OutputTokens
        );
    }
}

internal sealed record LiveMemoRecallEvidence(
    string Schema,
    string CaseLabel,
    int CallIndex,
    string ConnectionId,
    string Kind,
    string ModelId,
    string CompletionSurfaceId,
    string ClientName,
    string ApiSpecId,
    string PodId,
    int ActiveMemoCount,
    string FrozenPromptSha256,
    int FrozenPromptUtf8Bytes,
    int QueryUtf8Bytes,
    int MaxResults,
    int MaxPromptUtf8Bytes,
    int MaxTokens,
    int DelayMilliseconds,
    long ElapsedMilliseconds,
    string Outcome,
    string PromptCacheRequestStatus,
    string PromptCacheSupportStatus,
    string PromptCacheObservationStatus,
    long? UncachedInputTokens,
    long? CacheCreationInputTokens,
    long? CacheReadInputTokens,
    long? OutputTokens,
    int? SelectedCount,
    string[]? SelectedIds
);

internal static class LiveMemoRecallEvidenceSerializer {
    internal const string Schema =
        "atelia.memo-pod.deepseek-v4-flash-candidate.v1";

    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    internal static string Serialize(LiveMemoRecallEvidence evidence) {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Outcome is not (
                "completed" or "failed" or "cancelled"
            )
            || evidence.PromptCacheRequestStatus is not (
                "unknown" or "not-requested" or "requested"
            )
            || evidence.PromptCacheSupportStatus is not (
                "unknown" or "unsupported" or "supported"
            )
            || evidence.PromptCacheObservationStatus is not (
                "unknown" or "unavailable" or "partial" or "complete"
            )) {
            throw new InvalidOperationException(
                "Live MemoPod evidence contains an unknown closed status."
            );
        }
        return JsonSerializer.Serialize(evidence, Options);
    }

    internal static string Map(PromptCacheRequestStatus status)
        => status switch {
            PromptCacheRequestStatus.Unknown => "unknown",
            PromptCacheRequestStatus.NotRequested => "not-requested",
            PromptCacheRequestStatus.Requested => "requested",
            _ => throw new InvalidOperationException(
                "Unknown prompt-cache request status."
            )
        };

    internal static string Map(PromptCacheSupportStatus status)
        => status switch {
            PromptCacheSupportStatus.Unknown => "unknown",
            PromptCacheSupportStatus.Unsupported => "unsupported",
            PromptCacheSupportStatus.Supported => "supported",
            _ => throw new InvalidOperationException(
                "Unknown prompt-cache support status."
            )
        };

    internal static string Map(PromptCacheObservationStatus status)
        => status switch {
            PromptCacheObservationStatus.Unknown => "unknown",
            PromptCacheObservationStatus.Unavailable => "unavailable",
            PromptCacheObservationStatus.Partial => "partial",
            PromptCacheObservationStatus.Complete => "complete",
            _ => throw new InvalidOperationException(
                "Unknown prompt-cache observation status."
            )
        };
}

internal sealed class LiveMemoRecallConfigurationException : Exception;

internal sealed class LiveMemoRecallSafetyException : Exception;
