using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Diagnostics;

namespace Atelia.Galatea.Server;

internal sealed record GalateaDelegateDispatchRequest(
    string DispatchId,
    string? ThreadId,
    string Body
);

internal sealed record GalateaDelegateAcceptedHandle(
    string DispatchId,
    string ThreadId,
    string TurnId,
    Task<GalateaDelegateTerminal> Completion
);

internal abstract record GalateaDelegateTerminal(
    string DispatchId,
    string ThreadId,
    string TurnId
) {
    internal sealed record Completed(
        string DispatchId,
        string ThreadId,
        string TurnId,
        string Final
    ) : GalateaDelegateTerminal(DispatchId, ThreadId, TurnId);

    internal sealed record Failed(
        string DispatchId,
        string ThreadId,
        string TurnId,
        string Stage,
        string Code
    ) : GalateaDelegateTerminal(DispatchId, ThreadId, TurnId);
}

internal sealed class GalateaDelegateStartException
    : GalateaSidecarOperationException {
    internal GalateaDelegateStartException(string stage, string code)
        : base(
            $"Galatea delegate sidecar rejected dispatch at {stage}: {code}.",
            stage,
            code
        ) { }
}

internal interface IGalateaDelegateSidecar : IAsyncDisposable {
    Task<GalateaDelegateAcceptedHandle> StartAsync(
        GalateaDelegateDispatchRequest request,
        CancellationToken ct
    );
}

internal sealed partial class GalateaCodexSidecarClient
    : GalateaSidecarProcessClientBase, IGalateaDelegateSidecar {
    private const int ProtocolVersion = 1;
    private const int MaximumDispatchTombstones = 4_096;
    private const int AcceptanceStartupMarginMs = 5_000;
    private static readonly JsonSerializerOptions WireJson = new() {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HashSet<string> _dispatchTombstones = new(
        StringComparer.Ordinal
    );
    private readonly object _dispatchGate = new();

    internal GalateaCodexSidecarClient(
        GalateaDelegateConfig config,
        GalateaSidecarProcessTestHooks? processHooks = null
    ) : base(config, processHooks) { }

    internal bool HasStartedProcessForTest {
        get {
            return HasStartedProcess;
        }
    }

    internal int GenerationCountForTest {
        get {
            return GenerationCount;
        }
    }

    private DispatchClaim TryClaimDispatchForWrite(string dispatchId) {
        lock (_dispatchGate) {
            if (_dispatchTombstones.Contains(dispatchId)) {
                return DispatchClaim.Duplicate;
            }
            if (_dispatchTombstones.Count >= MaximumDispatchTombstones) {
                return DispatchClaim.CapacityExceeded;
            }
            _dispatchTombstones.Add(dispatchId);
            return DispatchClaim.Claimed;
        }
    }

    public async Task<GalateaDelegateAcceptedHandle> StartAsync(
        GalateaDelegateDispatchRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        ct.ThrowIfCancellationRequested();

        Generation generation = (Generation)await GetReadyGenerationAsync(ct)
            .ConfigureAwait(false);
        return await generation.DispatchAsync(request, ct)
            .ConfigureAwait(false);
    }

    internal static new TimeSpan ComputeReadyDeadline(int rpcTimeoutMs) =>
        GalateaSidecarProcessClientBase.ComputeReadyDeadline(rpcTimeoutMs);

    internal static TimeSpan ComputeAcceptanceDeadline(int rpcTimeoutMs) {
        if (rpcTimeoutMs is < 100 or > 300_000) {
            throw new ArgumentOutOfRangeException(nameof(rpcTimeoutMs));
        }
        long milliseconds = checked(
            5L * rpcTimeoutMs + AcceptanceStartupMarginMs
        );
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    internal ProcessStartInfo CreateStartInfoForTest() => CreateStartInfo();

    internal void ConfigureSidecarEnvironmentForTest(
        IDictionary<string, string?> environment
    ) => ConfigureSidecarEnvironment(environment);

    private void ValidateRequest(GalateaDelegateDispatchRequest request) {
        GalateaSidecarWire.RequireIdentifier(
            request.DispatchId,
            nameof(request.DispatchId)
        );
        if (request.ThreadId is not null) {
            GalateaSidecarWire.RequireIdentifier(
                request.ThreadId,
                nameof(request.ThreadId)
            );
        }
        if (string.IsNullOrWhiteSpace(request.Body)) {
            throw new ArgumentException(
                "Delegate task body must not be blank.",
                nameof(request)
            );
        }
        int bytes;
        try {
            bytes = GalateaSidecarWire.StrictUtf8.GetByteCount(request.Body);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Delegate task body must be valid Unicode.",
                nameof(request),
                exception
            );
        }
        if (bytes > Route.MaximumTaskUtf8Bytes) {
            throw new ArgumentException(
                "Delegate task body exceeds its UTF-8 byte limit.",
                nameof(request)
            );
        }
    }

    internal override void ProcessFrame(
        GalateaSidecarProcessGeneration processGeneration,
        byte[] line
    ) {
        var generation = (Generation)processGeneration;
        try {
            using JsonDocument document = JsonDocument.Parse(line, new() {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            JsonElement root = document.RootElement;
            Dictionary<string, JsonElement> properties =
                GalateaSidecarWire.ReadStrictProperties(root);
            GalateaSidecarWire.RequireProtocolVersion(
                properties,
                ProtocolVersion
            );
            string type = GalateaSidecarWire.RequireString(
                properties,
                "type"
            );
            if (!string.Equals(type, "ready", StringComparison.Ordinal)
                && !generation.IsReady) {
                throw new InvalidDataException(
                    "Sidecar sent a business frame before ready."
                );
            }
            switch (type) {
                case "ready":
                    GalateaSidecarWire.RequireExactKeys(
                        properties,
                        ["v", "type"]
                    );
                    if (!generation.TrySetReady()) {
                        throw new InvalidDataException(
                            "Sidecar sent ready more than once."
                        );
                    }
                    DebugUtil.Info(
                        LogCategory,
                        "Node sidecar ready; Codex app-server initialized: "
                            + $"generation={generation.Id}.",
                        eventKind: DebugEventKind.Success
                    );
                    break;
                case "accepted":
                    GalateaSidecarWire.RequireExactKeys(properties, [
                        "v", "type", "requestId", "dispatchId",
                        "threadId", "turnId"
                    ]);
                    string acceptedDispatchId = GalateaSidecarWire
                        .RequireIdentifier(
                        properties,
                        "dispatchId"
                    );
                    generation.Accept(
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "requestId"
                        ),
                        acceptedDispatchId,
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "threadId"
                        ),
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "turnId"
                        )
                    );
                    DebugUtil.Info(
                        LogCategory,
                        "Node sidecar reports Codex turn accepted: "
                            + $"generation={generation.Id}, dispatchId={acceptedDispatchId}.",
                        eventKind: DebugEventKind.Success
                    );
                    break;
                case "completed":
                    GalateaSidecarWire.RequireExactKeys(properties, [
                        "v", "type", "dispatchId", "threadId", "turnId",
                        "final"
                    ]);
                    string final = GalateaSidecarWire.RequireString(
                        properties,
                        "final"
                    );
                    int finalBytes = GalateaSidecarWire.StrictUtf8
                        .GetByteCount(final);
                    if (finalBytes == 0
                        || finalBytes > Route.MaximumReplyUtf8Bytes) {
                        throw new InvalidDataException(
                            "Sidecar completed final violates its UTF-8 bound."
                        );
                    }
                    string completedDispatchId = GalateaSidecarWire
                        .RequireIdentifier(
                        properties,
                        "dispatchId"
                    );
                    generation.Complete(
                        completedDispatchId,
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "threadId"
                        ),
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "turnId"
                        ),
                        final
                    );
                    DebugUtil.Info(
                        LogCategory,
                        "Node sidecar received Codex final: "
                            + $"generation={generation.Id}, dispatchId={completedDispatchId}, "
                            + $"finalUtf8Bytes={finalBytes}.",
                        eventKind: DebugEventKind.Success
                    );
                    break;
                case "failed":
                    ProcessFailedFrame(generation, properties);
                    break;
                default:
                    throw new InvalidDataException(
                        "Sidecar frame has an unknown type."
                    );
            }
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidDataException
                or EncoderFallbackException) {
            FailGeneration(
                generation,
                "protocol",
                "SIDECAR_PROTOCOL_ERROR",
                graceful: false
            );
        }
    }

    private void ProcessFailedFrame(
        Generation generation,
        Dictionary<string, JsonElement> properties
    ) {
        GalateaSidecarWire.RequireAllowedKeys(properties, [
            "v", "type", "requestId", "dispatchId", "threadId",
            "turnId", "stage", "code"
        ]);
        GalateaSidecarWire.RequirePresent(
            properties,
            ["v", "type", "stage", "code"]
        );
        string stage = GalateaSidecarWire.RequireString(properties, "stage");
        if (stage is not ("protocol" or "start" or "turn" or "shutdown")) {
            throw new InvalidDataException("Sidecar failure stage is invalid.");
        }
        string code = GalateaSidecarWire.RequireIdentifier(properties, "code");
        string? requestId = GalateaSidecarWire.OptionalIdentifier(
            properties,
            "requestId"
        );
        string? dispatchId = GalateaSidecarWire.OptionalIdentifier(
            properties,
            "dispatchId"
        );
        string? threadId = GalateaSidecarWire.OptionalIdentifier(
            properties,
            "threadId"
        );
        string? turnId = GalateaSidecarWire.OptionalIdentifier(
            properties,
            "turnId"
        );
        if (!generation.FailCorrelated(
                requestId,
                dispatchId,
                threadId,
                turnId,
                stage,
                code
            )) {
            throw new InvalidDataException(
                "Sidecar failure frame is not correlated."
            );
        }
        DebugUtil.Info(
            LogCategory,
            "Node sidecar reports delegated Codex failure: "
                + $"generation={generation.Id}, "
                + $"dispatchId={dispatchId ?? "<request-level>"}, "
                + $"stage={stage}, code={code}.",
            eventKind: DebugEventKind.Failure
        );
    }

    protected override GalateaSidecarProcessGeneration CreateGeneration(
        int id,
        ProcessStartInfo startInfo
    ) => new Generation(this, id, startInfo);

    protected override Exception CreateFailureException(
        string stage,
        string code
    ) => new GalateaDelegateStartException(stage, code);

    private sealed class Generation : GalateaSidecarProcessGeneration {
        private readonly object _pendingGate = new();
        private readonly GalateaCodexSidecarClient _owner;
        private readonly Dictionary<string, PendingDispatch> _byRequest =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingDispatch> _byDispatch =
            new(StringComparer.Ordinal);

        internal Generation(
            GalateaCodexSidecarClient owner,
            int id,
            ProcessStartInfo startInfo
        ) : base(owner, id, startInfo) {
            _owner = owner;
        }

        internal async Task<GalateaDelegateAcceptedHandle> DispatchAsync(
            GalateaDelegateDispatchRequest request,
            CancellationToken ct
        ) {
            string requestId = Guid.NewGuid().ToString("N");
            PendingDispatch pending;
            bool ownsWrite;
            lock (_pendingGate) {
                if (IsFailed) {
                    throw CurrentFailure();
                }
                if (_byDispatch.TryGetValue(
                        request.DispatchId,
                        out pending!)) {
                    if (!string.Equals(
                            pending.RequestedThreadId,
                            request.ThreadId,
                            StringComparison.Ordinal
                        )
                        || !string.Equals(
                            pending.Body,
                            request.Body,
                            StringComparison.Ordinal
                        )) {
                        throw new ArgumentException(
                            "A duplicate dispatchId must carry the exact same "
                            + "threadId and task body.",
                            nameof(request)
                        );
                    }
                    ownsWrite = false;
                }
                else {
                    pending = new PendingDispatch(
                        requestId,
                        request.DispatchId,
                        request.ThreadId,
                        request.Body
                    );
                    if (!_byRequest.TryAdd(requestId, pending)) {
                        throw new InvalidOperationException(
                            "Duplicate delegate request identifier."
                        );
                    }
                    _byDispatch.Add(request.DispatchId, pending);
                    ownsWrite = true;
                }
            }

            if (!ownsWrite) {
                try {
                    return await pending.Accepted.Task.WaitAsync(
                            TimeSpan.FromMilliseconds(
                                _owner.ConfigForGeneration.Sidecar.RpcTimeoutMs
                            ),
                            ct
                        )
                        .ConfigureAwait(false);
                }
                catch (TimeoutException) {
                    throw new GalateaDelegateStartException(
                        "protocol",
                        "SIDECAR_ATTACHED_WAIT_TIMEOUT"
                    );
                }
            }

            byte[] frame = JsonSerializer.SerializeToUtf8Bytes(
                new DispatchWireFrame(
                    ProtocolVersion,
                    "dispatch",
                    requestId,
                    request.DispatchId,
                    request.ThreadId,
                    request.Body
                ),
                WireJson
            );
            if (frame.Length
                > _owner.ConfigForGeneration.Sidecar.MaximumFrameUtf8Bytes) {
                AbandonPending(
                    pending,
                    "protocol",
                    "SIDECAR_FRAME_TOO_LARGE"
                );
                throw new ArgumentException(
                    "Encoded delegate dispatch exceeds its frame limit.",
                    nameof(request)
                );
            }
            byte[] framed = GC.AllocateUninitializedArray<byte>(
                frame.Length + 1
            );
            frame.CopyTo(framed, 0);
            framed[^1] = (byte)'\n';
            try {
                await WriteFrameAsync(
                        framed,
                        ct,
                        () => {
                            DispatchClaim claim = _owner
                                .TryClaimDispatchForWrite(
                                    request.DispatchId
                                );
                            if (claim is DispatchClaim.Claimed) {
                                return;
                            }
                            string code = claim == DispatchClaim.Duplicate
                                ? "DUPLICATE_DISPATCH_ID"
                                : "DISPATCH_CAPACITY_EXCEEDED";
                            AbandonPending(pending, "protocol", code);
                            throw new GalateaDelegateStartException(
                                "protocol",
                                code
                            );
                        }
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                AbandonPending(
                    pending,
                    "protocol",
                    "SIDECAR_WRITE_CANCELLED_BEFORE_START"
                );
                throw;
            }
            catch (GalateaDelegateStartException exception) when (
                exception.Code == "SIDECAR_WRITE_GATE_TIMEOUT") {
                AbandonPending(pending, exception.Stage, exception.Code);
                throw;
            }
            catch (GalateaDelegateStartException exception) when (
                exception.Code is "SIDECAR_WRITE_OUTCOME_UNKNOWN"
                    or "SIDECAR_WRITE_FAILED") {
                ObserveAcceptedFault(pending);
                throw;
            }
            catch (GalateaDelegateStartException) {
                ObserveAcceptedFault(pending);
                throw;
            }
            try {
                return await pending.Accepted.Task.WaitAsync(
                        ComputeAcceptanceDeadline(
                            _owner.ConfigForGeneration.Sidecar.RpcTimeoutMs
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is TimeoutException
                    || exception is OperationCanceledException
                        && ct.IsCancellationRequested) {
                _owner.FailGenerationForGeneration(
                    this,
                    "protocol",
                    "SIDECAR_ACCEPTANCE_OUTCOME_UNKNOWN",
                    graceful: false
                );
                ObserveAcceptedFault(pending);
                throw new GalateaDelegateStartException(
                    "protocol",
                    "SIDECAR_ACCEPTANCE_OUTCOME_UNKNOWN"
                );
            }
        }

        internal void Accept(
            string requestId,
            string dispatchId,
            string threadId,
            string turnId
        ) {
            PendingDispatch pending;
            lock (_pendingGate) {
                if (!_byRequest.Remove(requestId, out pending!)
                    || !string.Equals(
                        pending.DispatchId,
                        dispatchId,
                        StringComparison.Ordinal
                    )
                    || pending.ThreadId is not null
                    || !_byDispatch.TryGetValue(
                        dispatchId,
                        out PendingDispatch? reserved
                    )
                    || !ReferenceEquals(reserved, pending)) {
                    throw new InvalidDataException(
                        "Sidecar accepted correlation is invalid."
                    );
                }
                pending.ThreadId = threadId;
                pending.TurnId = turnId;
            }
            pending.Accepted.TrySetResult(new(
                dispatchId,
                threadId,
                turnId,
                pending.Terminal.Task
            ));
        }

        internal void Complete(
            string dispatchId,
            string threadId,
            string turnId,
            string final
        ) {
            PendingDispatch pending = TakeAccepted(
                dispatchId,
                threadId,
                turnId
            );
            pending.Terminal.TrySetResult(
                new GalateaDelegateTerminal.Completed(
                    dispatchId,
                    threadId,
                    turnId,
                    final
                )
            );
        }

        internal bool FailCorrelated(
            string? requestId,
            string? dispatchId,
            string? threadId,
            string? turnId,
            string stage,
            string code
        ) {
            PendingDispatch? pending = null;
            bool accepted = false;
            lock (_pendingGate) {
                if (requestId is not null
                    && _byRequest.TryGetValue(requestId, out pending)) {
                    if (stage == "turn"
                        || (dispatchId is not null
                        && !string.Equals(
                            pending.DispatchId,
                            dispatchId,
                            StringComparison.Ordinal
                        ))) {
                        return false;
                    }
                    _byRequest.Remove(requestId);
                    _byDispatch.Remove(pending.DispatchId);
                }
                else if (dispatchId is not null
                    && _byDispatch.TryGetValue(dispatchId, out pending)) {
                    accepted = true;
                    if (stage is not ("turn" or "shutdown")
                        || (requestId is not null
                        && !string.Equals(
                            pending.RequestId,
                            requestId,
                            StringComparison.Ordinal
                        ))) {
                        return false;
                    }
                    if ((threadId is not null
                            && !string.Equals(
                                pending.ThreadId,
                                threadId,
                                StringComparison.Ordinal
                            ))
                        || (turnId is not null
                            && !string.Equals(
                                pending.TurnId,
                                turnId,
                                StringComparison.Ordinal
                            ))) {
                        return false;
                    }
                    _byDispatch.Remove(dispatchId);
                }
            }
            if (pending is null) {
                return false;
            }
            if (!accepted) {
                pending.Accepted.TrySetException(
                    new GalateaDelegateStartException(stage, code)
                );
            }
            else {
                pending.Terminal.TrySetResult(
                    new GalateaDelegateTerminal.Failed(
                        pending.DispatchId,
                        pending.ThreadId!,
                        pending.TurnId!,
                        stage,
                        code
                    )
                );
            }
            return true;
        }

        protected override void CompletePendingFailure(
            string stage,
            string code
        ) {
            PendingDispatch[] pending;
            lock (_pendingGate) {
                pending = [
                    .. _byRequest.Values,
                    .. _byDispatch.Values
                ];
                _byRequest.Clear();
                _byDispatch.Clear();
            }
            foreach (PendingDispatch item in pending.Distinct()) {
                if (item.ThreadId is null || item.TurnId is null) {
                    item.Accepted.TrySetException(
                        new GalateaDelegateStartException(stage, code)
                    );
                }
                else {
                    item.Terminal.TrySetResult(
                        new GalateaDelegateTerminal.Failed(
                            item.DispatchId,
                            item.ThreadId,
                            item.TurnId,
                            stage,
                            code
                        )
                    );
                }
            }
        }

        private PendingDispatch TakeAccepted(
            string dispatchId,
            string threadId,
            string turnId
        ) {
            lock (_pendingGate) {
                if (!_byDispatch.Remove(dispatchId, out PendingDispatch? pending)
                    || !string.Equals(
                        pending.ThreadId,
                        threadId,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        pending.TurnId,
                        turnId,
                        StringComparison.Ordinal
                    )) {
                    if (pending is not null) {
                        _byDispatch.Add(dispatchId, pending);
                    }
                    throw new InvalidDataException(
                        "Sidecar terminal correlation is invalid."
                    );
                }
                return pending;
            }
        }

        private void AbandonPending(
            PendingDispatch pending,
            string stage,
            string code
        ) {
            lock (_pendingGate) {
                _byRequest.Remove(pending.RequestId);
                _byDispatch.Remove(pending.DispatchId);
            }
            pending.Accepted.TrySetException(
                new GalateaDelegateStartException(stage, code)
            );
            _ = pending.Accepted.Task.Exception;
        }

        private static void ObserveAcceptedFault(PendingDispatch pending) {
            _ = pending.Accepted.Task.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }

    }

    private sealed class PendingDispatch(
        string requestId,
        string dispatchId,
        string? requestedThreadId,
        string body
    ) {
        internal string RequestId { get; } = requestId;
        internal string DispatchId { get; } = dispatchId;
        internal string? RequestedThreadId { get; } = requestedThreadId;
        internal string Body { get; } = body;
        internal string? ThreadId { get; set; }
        internal string? TurnId { get; set; }
        internal TaskCompletionSource<GalateaDelegateAcceptedHandle>
            Accepted { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        internal TaskCompletionSource<GalateaDelegateTerminal>
            Terminal { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
    }

    private sealed record DispatchWireFrame(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("dispatchId")] string DispatchId,
        [property: JsonPropertyName("threadId")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ThreadId,
        [property: JsonPropertyName("task")] string Task
    );

    private enum DispatchClaim {
        Claimed,
        Duplicate,
        CapacityExceeded
    }
}
