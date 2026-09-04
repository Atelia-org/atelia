using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Diagnostics;

namespace Atelia.Galatea.Server;

/// <summary>
/// Production exact V3 transport for durable Codex delegation. The durable
/// store and driver own business correlation and recovery state; this client
/// owns only the exact sidecar protocol and process transport.
/// </summary>
internal sealed class GalateaCodexDurableSidecarClient
    : GalateaSidecarProcessClientBase, IGalateaDurableDelegateTransport {
    private const int ProtocolVersion = 3;
    private const int OperationStartupMarginMs = 5_000;
    private const int BindingRpcBudgetCount = 5;
    private const int StartTurnRpcBudgetCount = 5;
    private const int MaximumOperationTombstones = 4_096;

    private static readonly JsonSerializerOptions WireJson = new() {
        PropertyNamingPolicy = null
    };

    private static readonly HashSet<string> InspectionFailureCodes = new(
        StringComparer.Ordinal
    ) {
        "TURN_FAILED",
        "TURN_INTERRUPTED",
        "FINAL_MISSING",
        "FINAL_BLANK",
        "FINAL_INVALID_UNICODE",
        "FINAL_TOO_LARGE"
    };

    private static readonly HashSet<string> InspectionAmbiguityCodes = new(
        StringComparer.Ordinal
    ) {
        "THREAD_NOT_FOUND",
        "THREAD_ID_MISMATCH",
        "THREAD_OWNERSHIP_MISMATCH",
        "THREAD_CWD_MISMATCH",
        "THREAD_SHAPE_INVALID",
        "INSPECTION_LIMIT_EXCEEDED",
        "TURN_ID_INVALID",
        "TURN_ID_NOT_UNIQUE",
        "TURN_ITEMS_INCOMPLETE",
        "TURN_ITEMS_INVALID",
        "ITEM_ID_INVALID",
        "ITEM_ID_NOT_UNIQUE",
        "DISPATCH_ID_NOT_UNIQUE",
        "DISPATCH_BODY_MISMATCH",
        "TURN_STATUS_INVALID",
        "FINAL_AMBIGUOUS"
    };

    private readonly object _operationGate = new();
    private readonly HashSet<string> _startTombstones = new(
        StringComparer.Ordinal
    );

    internal GalateaCodexDurableSidecarClient(
        GalateaDelegateConfig config,
        GalateaSidecarProcessTestHooks? processHooks = null
    ) : base(config, processHooks) { }

    internal bool HasStartedProcessForTest => HasStartedProcess;
    internal int GenerationCountForTest => GenerationCount;
    internal ProcessStartInfo CreateStartInfoForTest() => CreateStartInfo();

    public async Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
        GalateaEnsureDelegateBindingRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        GalateaSidecarWire.RequireIdentifier(
            request.BindingOperationId,
            nameof(request.BindingOperationId)
        );
        ct.ThrowIfCancellationRequested();
        var generation = (Generation)await GetReadyGenerationAsync(ct)
            .ConfigureAwait(false);
        string requestId = Guid.NewGuid().ToString("N");
        var pending = new PendingBinding(
            requestId,
            request.BindingOperationId
        );
        return await generation.ExecuteAsync(
                pending,
                SerializeFrame(new EnsureBindingWireFrame(
                    ProtocolVersion,
                    "ensure-binding",
                    requestId,
                    request.BindingOperationId
                )),
                () => generation.ClaimActiveBinding(pending),
                ComputeBindingDeadline(Config.Sidecar.RpcTimeoutMs),
                "ensure-binding",
                "BINDING_OUTCOME_UNKNOWN",
                detachOnCallerCancellation: false,
                ct
            )
            .ConfigureAwait(false);
    }

    public async Task<GalateaDelegateTurnAccepted> StartTurnAsync(
        GalateaStartDelegateTurnRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDispatchRequest(
            request.DispatchId,
            request.ThreadId,
            request.Task
        );
        ct.ThrowIfCancellationRequested();
        var generation = (Generation)await GetReadyGenerationAsync(ct)
            .ConfigureAwait(false);
        string requestId = Guid.NewGuid().ToString("N");
        var pending = new PendingStart(
            requestId,
            request.DispatchId,
            request.ThreadId,
            request.Task
        );
        return await generation.ExecuteAsync(
                pending,
                SerializeFrame(new StartTurnWireFrame(
                    ProtocolVersion,
                    "start-turn",
                    requestId,
                    request.DispatchId,
                    request.ThreadId,
                    request.Task
                )),
                () => ClaimStart(request.DispatchId),
                ComputeStartTurnDeadline(Config.Sidecar.RpcTimeoutMs),
                "start-turn",
                "START_OUTCOME_UNKNOWN",
                detachOnCallerCancellation: false,
                ct
            )
            .ConfigureAwait(false);
    }

    public async Task<GalateaDelegateDispatchInspection>
        InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request,
            CancellationToken ct
        ) {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDispatchRequest(
            request.DispatchId,
            request.ThreadId,
            request.Task
        );
        if (request.ExpectedTurnId is { } expectedTurnId) {
            GalateaSidecarWire.RequireIdentifier(
                expectedTurnId,
                nameof(request.ExpectedTurnId)
            );
        }
        ct.ThrowIfCancellationRequested();
        var generation = (Generation)await GetReadyGenerationAsync(ct)
            .ConfigureAwait(false);
        string requestId = Guid.NewGuid().ToString("N");
        var pending = new PendingInspection(
            requestId,
            request.DispatchId,
            request.ThreadId,
            request.Task,
            request.ExpectedTurnId
        );
        return await generation.ExecuteAsync(
                pending,
                SerializeFrame(new InspectDispatchWireFrame(
                    ProtocolVersion,
                    "inspect-dispatch",
                    requestId,
                    request.DispatchId,
                    request.ThreadId,
                    request.Task,
                    request.ExpectedTurnId
                )),
                beforeWrite: null,
                responseDeadline: null,
                "inspect-dispatch",
                "INSPECTION_UNAVAILABLE",
                detachOnCallerCancellation: true,
                ct
            )
            .ConfigureAwait(false);
    }

    internal static TimeSpan ComputeBindingDeadline(int rpcTimeoutMs) =>
        ComputeOperationDeadline(rpcTimeoutMs, BindingRpcBudgetCount);

    internal static TimeSpan ComputeStartTurnDeadline(int rpcTimeoutMs) =>
        ComputeOperationDeadline(rpcTimeoutMs, StartTurnRpcBudgetCount);

    protected override GalateaSidecarProcessGeneration CreateGeneration(
        int id,
        ProcessStartInfo startInfo
    ) => new Generation(this, id, startInfo);

    protected override Exception CreateFailureException(
        string stage,
        string code
    ) => new GalateaDurableDelegateTransportException(stage, code);

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
            Dictionary<string, JsonElement> properties = GalateaSidecarWire
                .ReadStrictProperties(document.RootElement);
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
                        "Node durable sidecar ready: "
                            + $"generation={generation.Id}.",
                        eventKind: DebugEventKind.Success
                    );
                    return;
                case "binding-established":
                    GalateaSidecarWire.RequireExactKeys(properties, [
                        "v", "type", "requestId", "bindingOperationId",
                        "threadId"
                    ]);
                    generation.CompleteBinding(
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "requestId"
                        ),
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "bindingOperationId"
                        ),
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "threadId"
                        )
                    );
                    return;
                case "turn-accepted":
                    GalateaSidecarWire.RequireExactKeys(properties, [
                        "v", "type", "requestId", "dispatchId",
                        "threadId", "turnId"
                    ]);
                    generation.CompleteStart(
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "requestId"
                        ),
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "dispatchId"
                        ),
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "threadId"
                        ),
                        GalateaSidecarWire.RequireIdentifier(
                            properties,
                            "turnId"
                        )
                    );
                    return;
                case "dispatch-inspected":
                    ProcessInspection(generation, properties);
                    return;
                case "failed":
                    ProcessFailure(generation, properties);
                    return;
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

    private void ProcessInspection(
        Generation generation,
        Dictionary<string, JsonElement> properties
    ) {
        string outcome = GalateaSidecarWire.RequireString(
            properties,
            "outcome"
        );
        string requestId = GalateaSidecarWire.RequireIdentifier(
            properties,
            "requestId"
        );
        string dispatchId = GalateaSidecarWire.RequireIdentifier(
            properties,
            "dispatchId"
        );
        string threadId = GalateaSidecarWire.RequireIdentifier(
            properties,
            "threadId"
        );
        GalateaDelegateInspectionSource source = RequireInspectionSource(
            properties
        );
        switch (outcome) {
            case "not-found":
                GalateaSidecarWire.RequireExactKeys(properties, [
                    "v", "type", "requestId", "dispatchId", "threadId",
                    "outcome", "source"
                ]);
                RequirePersistentSource(source, outcome);
                generation.CompleteInspection(
                    requestId,
                    dispatchId,
                    threadId,
                    new GalateaDelegateDispatchInspection.NotFound(
                        dispatchId,
                        threadId,
                        source
                    )
                );
                return;
            case "running": {
                GalateaSidecarWire.RequireExactKeys(properties, [
                    "v", "type", "requestId", "dispatchId", "threadId",
                    "outcome", "turnId", "source"
                ]);
                string turnId = GalateaSidecarWire.RequireIdentifier(
                    properties,
                    "turnId"
                );
                generation.CompleteInspection(
                    requestId,
                    dispatchId,
                    threadId,
                    new GalateaDelegateDispatchInspection.Running(
                        dispatchId,
                        threadId,
                        turnId,
                        source
                    )
                );
                return;
            }
            case "completed": {
                GalateaSidecarWire.RequireExactKeys(properties, [
                    "v", "type", "requestId", "dispatchId", "threadId",
                    "outcome", "turnId", "final", "source"
                ]);
                string turnId = GalateaSidecarWire.RequireIdentifier(
                    properties,
                    "turnId"
                );
                string final = GalateaSidecarWire.RequireString(
                    properties,
                    "final"
                );
                int finalBytes = GalateaSidecarWire.StrictUtf8
                    .GetByteCount(final);
                if (string.IsNullOrWhiteSpace(final)
                    || finalBytes > Route.MaximumReplyUtf8Bytes) {
                    throw new InvalidDataException(
                        "Durable sidecar final violates its text bound."
                    );
                }
                generation.CompleteInspection(
                    requestId,
                    dispatchId,
                    threadId,
                    new GalateaDelegateDispatchInspection.Completed(
                        dispatchId,
                        threadId,
                        turnId,
                        final,
                        source
                    )
                );
                return;
            }
            case "failed": {
                GalateaSidecarWire.RequireExactKeys(properties, [
                    "v", "type", "requestId", "dispatchId", "threadId",
                    "outcome", "turnId", "code", "source"
                ]);
                string code = RequireClosedCode(
                    properties,
                    InspectionFailureCodes,
                    "inspection failure"
                );
                string turnId = GalateaSidecarWire.RequireIdentifier(
                    properties,
                    "turnId"
                );
                generation.CompleteInspection(
                    requestId,
                    dispatchId,
                    threadId,
                    new GalateaDelegateDispatchInspection.Failed(
                        dispatchId,
                        threadId,
                        turnId,
                        code,
                        source
                    )
                );
                return;
            }
            case "ambiguous": {
                GalateaSidecarWire.RequireExactKeys(properties, [
                    "v", "type", "requestId", "dispatchId", "threadId",
                    "outcome", "code", "source"
                ]);
                string code = RequireClosedCode(
                    properties,
                    InspectionAmbiguityCodes,
                    "inspection ambiguity"
                );
                generation.CompleteInspection(
                    requestId,
                    dispatchId,
                    threadId,
                    new GalateaDelegateDispatchInspection.Ambiguous(
                        dispatchId,
                        threadId,
                        code,
                        source
                    )
                );
                return;
            }
            case "unavailable": {
                GalateaSidecarWire.RequireExactKeys(properties, [
                    "v", "type", "requestId", "dispatchId", "threadId",
                    "outcome", "turnId", "code", "source"
                ]);
                RequirePersistentSource(source, outcome);
                string code = GalateaSidecarWire.RequireIdentifier(
                    properties,
                    "code"
                );
                if (!string.Equals(
                        code,
                        GalateaDelegateDispatchInspection
                            .AcceptedTurnNotVisible.FailureCode,
                        StringComparison.Ordinal)) {
                    throw new InvalidDataException(
                        "Durable sidecar inspection unavailable code is invalid."
                    );
                }
                string turnId = GalateaSidecarWire.RequireIdentifier(
                    properties,
                    "turnId"
                );
                generation.CompleteInspection(
                    requestId,
                    dispatchId,
                    threadId,
                    new GalateaDelegateDispatchInspection
                        .AcceptedTurnNotVisible(
                            dispatchId,
                            threadId,
                            turnId,
                            source
                        )
                );
                return;
            }
            default:
                throw new InvalidDataException(
                    "Durable sidecar inspection outcome is invalid."
                );
        }
    }

    private static GalateaDelegateInspectionSource RequireInspectionSource(
        Dictionary<string, JsonElement> properties
    ) => GalateaSidecarWire.RequireString(properties, "source") switch {
        "live" => GalateaDelegateInspectionSource.Live,
        "persistent" => GalateaDelegateInspectionSource.Persistent,
        _ => throw new InvalidDataException(
            "Durable sidecar inspection source is invalid."
        )
    };

    private static void RequirePersistentSource(
        GalateaDelegateInspectionSource source,
        string outcome
    ) {
        if (source != GalateaDelegateInspectionSource.Persistent) {
            throw new InvalidDataException(
                $"Durable sidecar {outcome} must come from persistent inspection."
            );
        }
    }

    private static void ProcessFailure(
        Generation generation,
        Dictionary<string, JsonElement> properties
    ) {
        string stage = GalateaSidecarWire.RequireString(properties, "stage");
        string code = GalateaSidecarWire.RequireIdentifier(properties, "code");
        if (stage == "protocol") {
            GalateaSidecarWire.RequireAllowedKeys(properties, [
                "v", "type", "stage", "code", "requestId"
            ]);
            GalateaSidecarWire.RequirePresent(
                properties,
                ["v", "type", "stage", "code"]
            );
            string? requestId = GalateaSidecarWire.OptionalIdentifier(
                properties,
                "requestId"
            );
            if (requestId is null
                || !generation.FailRequest(requestId, stage, code)) {
                throw new InvalidDataException(
                    "Durable sidecar protocol failure is not correlated."
                );
            }
            return;
        }
        if (stage == "ensure-binding") {
            GalateaSidecarWire.RequireExactKeys(properties, [
                "v", "type", "stage", "requestId", "bindingOperationId",
                "code"
            ]);
            if (!generation.FailBinding(
                    GalateaSidecarWire.RequireIdentifier(
                        properties,
                        "requestId"
                    ),
                    GalateaSidecarWire.RequireIdentifier(
                        properties,
                        "bindingOperationId"
                    ),
                    stage,
                    code
                )) {
                throw new InvalidDataException(
                    "Durable binding failure is not correlated."
                );
            }
            return;
        }
        if (stage is "start-turn" or "inspect-dispatch" or "shutdown") {
            GalateaSidecarWire.RequireExactKeys(properties, [
                "v", "type", "stage", "requestId", "dispatchId",
                "threadId", "code"
            ]);
            if (!generation.FailDispatch(
                    GalateaSidecarWire.RequireIdentifier(
                        properties,
                        "requestId"
                    ),
                    GalateaSidecarWire.RequireIdentifier(
                        properties,
                        "dispatchId"
                    ),
                    GalateaSidecarWire.RequireIdentifier(
                        properties,
                        "threadId"
                    ),
                    stage,
                    code
                )) {
                throw new InvalidDataException(
                    "Durable dispatch failure is not correlated."
                );
            }
            return;
        }
        throw new InvalidDataException(
            "Durable sidecar failure stage is invalid."
        );
    }

    private OperationClaim ClaimStart(string dispatchId) {
        lock (_operationGate) {
            return Claim(_startTombstones, dispatchId);
        }
    }

    private static OperationClaim Claim(
        HashSet<string> tombstones,
        string identity
    ) {
        if (tombstones.Contains(identity)) {
            return OperationClaim.Duplicate;
        }
        if (tombstones.Count >= MaximumOperationTombstones) {
            return OperationClaim.CapacityExceeded;
        }
        tombstones.Add(identity);
        return OperationClaim.Claimed;
    }

    private byte[] SerializeFrame<T>(T frame) where T : notnull {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(frame, WireJson);
        if (json.Length > Config.Sidecar.MaximumFrameUtf8Bytes) {
            throw new ArgumentException(
                "Encoded durable sidecar request exceeds its frame limit.",
                nameof(frame)
            );
        }
        byte[] framed = GC.AllocateUninitializedArray<byte>(json.Length + 1);
        json.CopyTo(framed, 0);
        framed[^1] = (byte)'\n';
        return framed;
    }

    private void ValidateDispatchRequest(
        string dispatchId,
        string threadId,
        string task
    ) {
        GalateaSidecarWire.RequireIdentifier(dispatchId, nameof(dispatchId));
        GalateaSidecarWire.RequireIdentifier(threadId, nameof(threadId));
        if (string.IsNullOrWhiteSpace(task)) {
            throw new ArgumentException(
                "Durable delegate task must not be blank.",
                nameof(task)
            );
        }
        int bytes;
        try {
            bytes = GalateaSidecarWire.StrictUtf8.GetByteCount(task);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Durable delegate task must be valid Unicode.",
                nameof(task),
                exception
            );
        }
        if (bytes > Route.MaximumTaskUtf8Bytes) {
            throw new ArgumentException(
                "Durable delegate task exceeds its UTF-8 byte limit.",
                nameof(task)
            );
        }
    }

    private static string RequireClosedCode(
        Dictionary<string, JsonElement> properties,
        HashSet<string> allowed,
        string scope
    ) {
        string code = GalateaSidecarWire.RequireIdentifier(properties, "code");
        return allowed.Contains(code)
            ? code
            : throw new InvalidDataException(
                $"Durable sidecar {scope} code is invalid."
            );
    }

    private static TimeSpan ComputeOperationDeadline(
        int rpcTimeoutMs,
        int rpcCount
    ) {
        if (rpcTimeoutMs is < 100 or > 300_000) {
            throw new ArgumentOutOfRangeException(nameof(rpcTimeoutMs));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(rpcCount, 1);
        long milliseconds = checked(
            (long)rpcCount * rpcTimeoutMs + OperationStartupMarginMs
        );
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private sealed class Generation : GalateaSidecarProcessGeneration {
        private readonly object _pendingGate = new();
        private readonly GalateaCodexDurableSidecarClient _owner;
        private readonly Dictionary<string, PendingRequest> _pending = new(
            StringComparer.Ordinal
        );
        private readonly HashSet<string> _activeBindings = new(
            StringComparer.Ordinal
        );

        internal Generation(
            GalateaCodexDurableSidecarClient owner,
            int id,
            ProcessStartInfo startInfo
        ) : base(owner, id, startInfo) {
            _owner = owner;
        }

        internal async Task<T> ExecuteAsync<T>(
            PendingRequest<T> pending,
            byte[] frame,
            Func<OperationClaim>? beforeWrite,
            TimeSpan? responseDeadline,
            string stage,
            string outcomeUnknownCode,
            bool detachOnCallerCancellation,
            CancellationToken ct
        ) {
            lock (_pendingGate) {
                if (IsFailed) {
                    throw CurrentFailure();
                }
                if (!_pending.TryAdd(pending.RequestId, pending)) {
                    throw new InvalidOperationException(
                        "Duplicate durable sidecar request identifier."
                    );
                }
            }
            try {
                await WriteFrameAsync(
                        frame,
                        ct,
                        beforeWrite is null
                            ? null
                            : () => {
                                OperationClaim claim = beforeWrite();
                                if (claim is OperationClaim.Claimed) {
                                    return;
                                }
                                string code = claim == OperationClaim.Duplicate
                                    ? pending.DuplicateCode
                                    : "OPERATION_CAPACITY_EXCEEDED";
                                RemoveAndFail(pending, "protocol", code);
                                throw new GalateaDurableDelegateTransportException(
                                    "protocol",
                                    code
                                );
                            }
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                RemoveAndFail(
                    pending,
                    "protocol",
                    "SIDECAR_WRITE_CANCELLED_BEFORE_START"
                );
                throw;
            }
            catch (GalateaDurableDelegateTransportException exception) when (
                exception.Code == "SIDECAR_WRITE_GATE_TIMEOUT") {
                RemoveAndFail(pending, exception.Stage, exception.Code);
                throw;
            }
            catch (GalateaDurableDelegateTransportException exception) when (
                exception.Code is "SIDECAR_WRITE_OUTCOME_UNKNOWN"
                    or "SIDECAR_WRITE_FAILED") {
                ObservePendingFault(pending);
                throw new GalateaDurableDelegateTransportException(
                    stage,
                    outcomeUnknownCode
                );
            }
            catch (GalateaDurableDelegateTransportException) {
                ObservePendingFault(pending);
                throw;
            }

            if (!detachOnCallerCancellation) {
                return await AwaitAttachedResponseAsync(
                        pending,
                        responseDeadline,
                        stage,
                        outcomeUnknownCode,
                        ct
                    )
                    .ConfigureAwait(false);
            }

            Task<T> response = AwaitDetachedResponseAsync(
                pending,
                responseDeadline,
                stage,
                outcomeUnknownCode
            );
            try {
                return await response.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                ct.IsCancellationRequested) {
                ObserveTaskFault(response);
                throw;
            }
        }

        private async Task<T> AwaitAttachedResponseAsync<T>(
            PendingRequest<T> pending,
            TimeSpan? responseDeadline,
            string stage,
            string outcomeUnknownCode,
            CancellationToken ct
        ) {
            try {
                return responseDeadline is { } deadline
                    ? await pending.Completion.Task.WaitAsync(deadline, ct)
                        .ConfigureAwait(false)
                    : await pending.Completion.Task.WaitAsync(ct)
                        .ConfigureAwait(false);
            }
            catch (GenerationRequestFailedException) {
                ObservePendingFault(pending);
                throw new GalateaDurableDelegateTransportException(
                    stage,
                    outcomeUnknownCode
                );
            }
            catch (Exception exception) when (
                exception is TimeoutException
                    || exception is OperationCanceledException
                        && ct.IsCancellationRequested) {
                _owner.FailGenerationForGeneration(
                    this,
                    stage,
                    outcomeUnknownCode,
                    graceful: false
                );
                ObservePendingFault(pending);
                throw new GalateaDurableDelegateTransportException(
                    stage,
                    outcomeUnknownCode
                );
            }
        }

        private async Task<T> AwaitDetachedResponseAsync<T>(
            PendingRequest<T> pending,
            TimeSpan? responseDeadline,
            string stage,
            string outcomeUnknownCode
        ) {
            try {
                return responseDeadline is { } deadline
                    ? await pending.Completion.Task.WaitAsync(deadline)
                        .ConfigureAwait(false)
                    : await pending.Completion.Task.ConfigureAwait(false);
            }
            catch (GenerationRequestFailedException) {
                ObservePendingFault(pending);
                throw new GalateaDurableDelegateTransportException(
                    stage,
                    outcomeUnknownCode
                );
            }
            catch (TimeoutException) {
                _owner.FailGenerationForGeneration(
                    this,
                    stage,
                    outcomeUnknownCode,
                    graceful: false
                );
                ObservePendingFault(pending);
                throw new GalateaDurableDelegateTransportException(
                    stage,
                    outcomeUnknownCode
                );
            }
        }

        internal OperationClaim ClaimActiveBinding(PendingBinding pending) {
            lock (_pendingGate) {
                if (!_activeBindings.Add(pending.BindingOperationId)) {
                    return OperationClaim.Duplicate;
                }
                pending.OwnsActiveBindingClaim = true;
                return OperationClaim.Claimed;
            }
        }

        internal void CompleteBinding(
            string requestId,
            string bindingOperationId,
            string threadId
        ) {
            PendingBinding pending = Take<PendingBinding>(requestId);
            if (!string.Equals(
                    pending.BindingOperationId,
                    bindingOperationId,
                    StringComparison.Ordinal)) {
                Restore(pending);
                throw new InvalidDataException(
                    "Durable binding response identity is invalid."
                );
            }
            ReleaseBinding(pending);
            pending.Completion.TrySetResult(new(
                bindingOperationId,
                threadId
            ));
        }

        internal void CompleteStart(
            string requestId,
            string dispatchId,
            string threadId,
            string turnId
        ) {
            PendingStart pending = Take<PendingStart>(requestId);
            if (!pending.Matches(dispatchId, threadId)) {
                Restore(pending);
                throw new InvalidDataException(
                    "Durable start response identity is invalid."
                );
            }
            pending.Completion.TrySetResult(new(
                dispatchId,
                threadId,
                turnId
            ));
        }

        internal void CompleteInspection(
            string requestId,
            string dispatchId,
            string threadId,
            GalateaDelegateDispatchInspection result
        ) {
            PendingInspection pending = Take<PendingInspection>(requestId);
            if (!pending.Matches(dispatchId, threadId)
                || !pending.MatchesSelector(result)) {
                Restore(pending);
                throw new InvalidDataException(
                    "Durable inspection response identity or selector is invalid."
                );
            }
            pending.Completion.TrySetResult(result);
        }

        internal bool FailRequest(
            string requestId,
            string stage,
            string code
        ) {
            PendingRequest? pending = TakeAny(requestId);
            if (pending is null) {
                return false;
            }
            pending.Fail(new GalateaDurableDelegateTransportException(
                stage,
                code
            ));
            ReleaseBinding(pending);
            return true;
        }

        internal bool FailBinding(
            string requestId,
            string bindingOperationId,
            string stage,
            string code
        ) {
            PendingRequest? value = TakeAny(requestId);
            if (value is not PendingBinding pending
                || !string.Equals(
                    pending.BindingOperationId,
                    bindingOperationId,
                    StringComparison.Ordinal)) {
                if (value is not null) {
                    Restore(value);
                }
                return false;
            }
            pending.Fail(new GalateaDurableDelegateTransportException(
                stage,
                code
            ));
            ReleaseBinding(pending);
            return true;
        }

        internal bool FailDispatch(
            string requestId,
            string dispatchId,
            string threadId,
            string stage,
            string code
        ) {
            PendingRequest? pending = TakeAny(requestId);
            if (pending is not IPendingDispatch request
                || !request.Matches(dispatchId, threadId)
                || stage == "start-turn" && pending is not PendingStart
                || stage == "inspect-dispatch"
                    && pending is not PendingInspection) {
                if (pending is not null) {
                    Restore(pending);
                }
                return false;
            }
            pending.Fail(new GalateaDurableDelegateTransportException(
                stage,
                code
            ));
            return true;
        }

        protected override void CompletePendingFailure(
            string stage,
            string code
        ) {
            PendingRequest[] pending;
            lock (_pendingGate) {
                pending = [.. _pending.Values];
                _pending.Clear();
                _activeBindings.Clear();
            }
            foreach (PendingRequest request in pending) {
                request.Fail(new GenerationRequestFailedException(
                    stage,
                    code
                ));
            }
        }

        private TPending Take<TPending>(string requestId)
            where TPending : PendingRequest {
            PendingRequest? pending = TakeAny(requestId);
            if (pending is TPending typed) {
                return typed;
            }
            if (pending is not null) {
                Restore(pending);
            }
            throw new InvalidDataException(
                "Durable sidecar response kind is not correlated."
            );
        }

        private PendingRequest? TakeAny(string requestId) {
            lock (_pendingGate) {
                _pending.Remove(requestId, out PendingRequest? pending);
                return pending;
            }
        }

        private void Restore(PendingRequest pending) {
            lock (_pendingGate) {
                if (!_pending.TryAdd(pending.RequestId, pending)) {
                    throw new InvalidDataException(
                        "Durable pending request could not be restored."
                    );
                }
            }
        }

        private void RemoveAndFail(
            PendingRequest pending,
            string stage,
            string code
        ) {
            lock (_pendingGate) {
                _pending.Remove(pending.RequestId);
            }
            pending.Fail(new GalateaDurableDelegateTransportException(
                stage,
                code
            ));
            ReleaseBinding(pending);
            _ = pending.FaultTask.Exception;
        }

        private void ReleaseBinding(PendingRequest pending) {
            if (pending is not PendingBinding binding) {
                return;
            }
            lock (_pendingGate) {
                if (!binding.OwnsActiveBindingClaim) {
                    return;
                }
                _activeBindings.Remove(binding.BindingOperationId);
                binding.OwnsActiveBindingClaim = false;
            }
        }

        private static void ObservePendingFault(PendingRequest pending) {
            ObserveTaskFault(pending.FaultTask);
        }

        private static void ObserveTaskFault(Task task) {
            _ = task.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }

    private abstract class PendingRequest(string requestId) {
        internal string RequestId { get; } = requestId;
        internal abstract string DuplicateCode { get; }
        internal abstract Task FaultTask { get; }
        internal abstract void Fail(Exception exception);
    }

    private sealed class GenerationRequestFailedException(
        string stage,
        string code
    ) : Exception(
        $"Durable sidecar generation failed at {stage}: {code}."
    );

    private abstract class PendingRequest<T>(string requestId)
        : PendingRequest(requestId) {
        internal TaskCompletionSource<T> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal override Task FaultTask => Completion.Task;
        internal override void Fail(Exception exception) =>
            Completion.TrySetException(exception);
    }

    private sealed class PendingBinding(
        string requestId,
        string bindingOperationId
    ) : PendingRequest<GalateaDelegateBindingEstablished>(requestId) {
        internal string BindingOperationId { get; } = bindingOperationId;
        internal bool OwnsActiveBindingClaim { get; set; }
        internal override string DuplicateCode =>
            "DUPLICATE_BINDING_OPERATION_ID";
    }

    private interface IPendingDispatch {
        bool Matches(string dispatchId, string threadId);
    }

    private sealed class PendingStart(
        string requestId,
        string dispatchId,
        string threadId,
        string task
    ) : PendingRequest<GalateaDelegateTurnAccepted>(requestId),
        IPendingDispatch {
        private readonly PendingDispatchIdentity _identity = new(
            dispatchId,
            threadId,
            task
        );

        internal string DispatchId => _identity.DispatchId;
        internal string ThreadId => _identity.ThreadId;
        internal string Task => _identity.Task;
        internal override string DuplicateCode => "DUPLICATE_DISPATCH_ID";
        internal bool Matches(string dispatchId, string threadId) =>
            _identity.Matches(dispatchId, threadId);

        bool IPendingDispatch.Matches(string dispatchId, string threadId) =>
            Matches(dispatchId, threadId);
    }

    private sealed class PendingInspection(
        string requestId,
        string dispatchId,
        string threadId,
        string task,
        string? expectedTurnId
    ) : PendingRequest<GalateaDelegateDispatchInspection>(requestId),
        IPendingDispatch {
        private readonly PendingDispatchIdentity _identity = new(
            dispatchId,
            threadId,
            task
        );

        internal string DispatchId => _identity.DispatchId;
        internal string ThreadId => _identity.ThreadId;
        internal string Task => _identity.Task;
        internal string? ExpectedTurnId { get; } = expectedTurnId;
        internal override string DuplicateCode => "DUPLICATE_DISPATCH_ID";
        internal bool Matches(string dispatchId, string threadId) =>
            _identity.Matches(dispatchId, threadId);

        internal bool MatchesSelector(
            GalateaDelegateDispatchInspection result
        ) {
            if (ExpectedTurnId is null) {
                return result is not GalateaDelegateDispatchInspection
                    .AcceptedTurnNotVisible;
            }
            return result switch {
                GalateaDelegateDispatchInspection.NotFound => false,
                GalateaDelegateDispatchInspection.Running running =>
                    MatchesExpectedTurn(running.TurnId),
                GalateaDelegateDispatchInspection.Completed completed =>
                    MatchesExpectedTurn(completed.TurnId),
                GalateaDelegateDispatchInspection.Failed failed =>
                    MatchesExpectedTurn(failed.TurnId),
                GalateaDelegateDispatchInspection.AcceptedTurnNotVisible
                    unavailable => MatchesExpectedTurn(unavailable.TurnId),
                GalateaDelegateDispatchInspection.Ambiguous => true,
                _ => false
            };
        }

        private bool MatchesExpectedTurn(string turnId) => string.Equals(
            ExpectedTurnId,
            turnId,
            StringComparison.Ordinal
        );

        bool IPendingDispatch.Matches(string dispatchId, string threadId) =>
            Matches(dispatchId, threadId);
    }

    private sealed record PendingDispatchIdentity(
        string DispatchId,
        string ThreadId,
        string Task
    ) {
        internal bool Matches(string dispatchId, string threadId) =>
            string.Equals(DispatchId, dispatchId, StringComparison.Ordinal)
                && string.Equals(ThreadId, threadId, StringComparison.Ordinal);
    }

    private sealed record EnsureBindingWireFrame(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("bindingOperationId")]
        string BindingOperationId
    );

    private sealed record StartTurnWireFrame(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("dispatchId")] string DispatchId,
        [property: JsonPropertyName("threadId")] string ThreadId,
        [property: JsonPropertyName("task")] string Task
    );

    private sealed record InspectDispatchWireFrame(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("dispatchId")] string DispatchId,
        [property: JsonPropertyName("threadId")] string ThreadId,
        [property: JsonPropertyName("task")] string Task,
        [property: JsonPropertyName("expectedTurnId")]
        string? ExpectedTurnId
    );

    private enum OperationClaim {
        Claimed,
        Duplicate,
        CapacityExceeded
    }
}
