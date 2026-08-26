using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

internal sealed class GalateaDelegateStartException : Exception {
    internal GalateaDelegateStartException(string stage, string code)
        : base($"Galatea delegate sidecar rejected dispatch at {stage}: {code}.") {
        Stage = stage;
        Code = code;
    }

    internal string Stage { get; }
    internal string Code { get; }
}

internal interface IGalateaDelegateSidecar : IAsyncDisposable {
    Task<GalateaDelegateAcceptedHandle> StartAsync(
        GalateaDelegateDispatchRequest request,
        CancellationToken ct
    );
}

internal sealed partial class GalateaCodexSidecarClient
    : IGalateaDelegateSidecar {
    private const int ProtocolVersion = 1;
    private const string LogCategory = "Galatea.DelegateSidecar";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private static readonly JsonSerializerOptions WireJson = new() {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _stateGate = new();
    private readonly GalateaDelegateConfig _config;
    private readonly GalateaDelegateRouteConfig _route;
    private Generation? _generation;
    private Task _restartBarrier = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposed;
    private int _nextGeneration;

    internal GalateaCodexSidecarClient(GalateaDelegateConfig config) {
        GalateaDelegateConfigReader.Validate(config);
        _config = config;
        _route = config.CodexRoute;
    }

    internal bool HasStartedProcessForTest {
        get {
            lock (_stateGate) {
                return _nextGeneration != 0;
            }
        }
    }

    internal int GenerationCountForTest {
        get {
            lock (_stateGate) {
                return _nextGeneration;
            }
        }
    }

    public async Task<GalateaDelegateAcceptedHandle> StartAsync(
        GalateaDelegateDispatchRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        ct.ThrowIfCancellationRequested();

        Generation generation = await GetReadyGenerationAsync(ct)
            .ConfigureAwait(false);
        try {
            return await generation.DispatchAsync(request, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) {
            FailGeneration(
                generation,
                "protocol",
                "SIDECAR_ACCEPTANCE_TIMEOUT",
                graceful: false
            );
            throw new GalateaDelegateStartException(
                "protocol",
                "SIDECAR_ACCEPTANCE_TIMEOUT"
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            FailGeneration(
                generation,
                "protocol",
                "SIDECAR_ACCEPTANCE_CANCELLED",
                graceful: false
            );
            throw;
        }
    }

    public ValueTask DisposeAsync() {
        lock (_stateGate) {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task<Generation> GetReadyGenerationAsync(
        CancellationToken ct
    ) {
        while (true) {
            Generation? generation;
            Task barrier;
            lock (_stateGate) {
                ObjectDisposedException.ThrowIf(_disposed, this);
                generation = _generation;
                barrier = _restartBarrier;
                if (generation is null && barrier.IsCompleted) {
                    generation = new Generation(
                        this,
                        checked(++_nextGeneration),
                        CreateStartInfo()
                    );
                    _generation = generation;
                    try {
                        generation.Start();
                    }
                    catch {
                        _generation = null;
                        throw;
                    }
                }
            }
            if (generation is null) {
                await barrier.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }
            try {
                await generation.Ready.Task.WaitAsync(
                        TimeSpan.FromMilliseconds(
                            _config.Sidecar.RpcTimeoutMs
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
                Task? staleBarrier = null;
                lock (_stateGate) {
                    if (!ReferenceEquals(_generation, generation)
                        || generation.IsFailed) {
                        staleBarrier = _restartBarrier;
                    }
                }
                if (staleBarrier is not null) {
                    await staleBarrier.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }
                return generation;
            }
            catch (TimeoutException) {
                FailGeneration(
                    generation,
                    "protocol",
                    "SIDECAR_READY_TIMEOUT",
                    graceful: false
                );
                throw new GalateaDelegateStartException(
                    "protocol",
                    "SIDECAR_READY_TIMEOUT"
                );
            }
        }
    }

    private ProcessStartInfo CreateStartInfo() {
        GalateaDelegateSidecarConfig sidecar = _config.Sidecar;
        var startInfo = new ProcessStartInfo {
            FileName = sidecar.NodeCommand,
            WorkingDirectory = _route.Cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(sidecar.EntryPoint);
        startInfo.Environment["CODEX_BRIDGE_TRANSPORT"] = "stdio";
        startInfo.Environment["CODEX_BRIDGE_ALLOWED_ROOTS"] =
            JsonSerializer.Serialize(_config.AllowedRoots);
        startInfo.Environment["CODEX_BRIDGE_DEFAULT_CWD"] = _route.Cwd;
        startInfo.Environment["CODEX_BRIDGE_CODEX_COMMAND"] =
            sidecar.CodexCommand;
        startInfo.Environment["CODEX_BRIDGE_RPC_TIMEOUT_MS"] =
            sidecar.RpcTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["CODEX_BRIDGE_VERBOSE"] = "false";
        startInfo.Environment["GALATEA_CODEX_MODE"] =
            _route.Mode == GalateaDelegateMode.Work ? "work" : "research";
        startInfo.Environment["GALATEA_CODEX_NETWORK"] =
            _route.Network ? "true" : "false";
        startInfo.Environment["GALATEA_CODEX_TURN_DEADLINE_MS"] =
            sidecar.TurnTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["GALATEA_CODEX_MAX_INPUT_FRAME_BYTES"] =
            sidecar.MaximumFrameUtf8Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["GALATEA_CODEX_MAX_OUTPUT_FRAME_BYTES"] =
            sidecar.MaximumFrameUtf8Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["GALATEA_CODEX_MAX_TASK_BYTES"] =
            _route.MaximumTaskUtf8Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["GALATEA_CODEX_MAX_FINAL_BYTES"] =
            _route.MaximumReplyUtf8Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return startInfo;
    }

    private void ValidateRequest(GalateaDelegateDispatchRequest request) {
        RequireIdentifier(request.DispatchId, nameof(request.DispatchId));
        if (request.ThreadId is not null) {
            RequireIdentifier(request.ThreadId, nameof(request.ThreadId));
        }
        if (string.IsNullOrWhiteSpace(request.Body)) {
            throw new ArgumentException(
                "Delegate task body must not be blank.",
                nameof(request)
            );
        }
        int bytes;
        try {
            bytes = StrictUtf8.GetByteCount(request.Body);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Delegate task body must be valid Unicode.",
                nameof(request),
                exception
            );
        }
        if (bytes > _route.MaximumTaskUtf8Bytes) {
            throw new ArgumentException(
                "Delegate task body exceeds its UTF-8 byte limit.",
                nameof(request)
            );
        }
    }

    private static void RequireIdentifier(string value, string parameter) {
        if (string.IsNullOrEmpty(value)
            || StrictUtf8.GetByteCount(value) > 200
            || !IdentifierRegex().IsMatch(value)) {
            throw new ArgumentException(
                "Delegate identifiers must match [A-Za-z0-9][A-Za-z0-9._:-]* "
                + "and fit 200 UTF-8 bytes.",
                parameter
            );
        }
    }

    private void FailGeneration(
        Generation generation,
        string stage,
        string code,
        bool graceful
    ) {
        Task cleanup;
        lock (_stateGate) {
            if (!generation.TryMarkFailure()) {
                return;
            }
            cleanup = generation.TerminateAsync(graceful);
            generation.CleanupTask = cleanup;
            if (ReferenceEquals(_generation, generation)) {
                _generation = null;
                _restartBarrier = cleanup;
            }
        }
        generation.CompleteFailure(stage, code);
        _ = cleanup.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        DebugUtil.Warning(
            LogCategory,
            $"Sidecar generation failed: generation={generation.Id}, "
            + $"stage={stage}, code={code}."
        );
    }

    private async Task DisposeCoreAsync() {
        Generation? generation;
        Task barrier;
        lock (_stateGate) {
            if (_disposed) {
                generation = null;
                barrier = _restartBarrier;
            }
            else {
                _disposed = true;
                generation = _generation;
                _generation = null;
                barrier = _restartBarrier;
            }
        }
        if (generation is not null) {
            FailGeneration(
                generation,
                "shutdown",
                "SIDECAR_DISPOSED",
                graceful: true
            );
            await generation.CleanupTask.ConfigureAwait(false);
        }
        await barrier.ConfigureAwait(false);
    }

    private void ProcessFrame(Generation generation, byte[] line) {
        try {
            using JsonDocument document = JsonDocument.Parse(line, new() {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            JsonElement root = document.RootElement;
            Dictionary<string, JsonElement> properties =
                ReadStrictProperties(root);
            RequireProtocolVersion(properties);
            string type = RequireString(properties, "type");
            if (!string.Equals(type, "ready", StringComparison.Ordinal)
                && !generation.IsReady) {
                throw new InvalidDataException(
                    "Sidecar sent a business frame before ready."
                );
            }
            switch (type) {
                case "ready":
                    RequireExactKeys(properties, ["v", "type"]);
                    if (!generation.TrySetReady()) {
                        throw new InvalidDataException(
                            "Sidecar sent ready more than once."
                        );
                    }
                    break;
                case "accepted":
                    RequireExactKeys(properties, [
                        "v", "type", "requestId", "dispatchId",
                        "threadId", "turnId"
                    ]);
                    generation.Accept(
                        RequireWireIdentifier(properties, "requestId"),
                        RequireWireIdentifier(properties, "dispatchId"),
                        RequireWireIdentifier(properties, "threadId"),
                        RequireWireIdentifier(properties, "turnId")
                    );
                    break;
                case "completed":
                    RequireExactKeys(properties, [
                        "v", "type", "dispatchId", "threadId", "turnId",
                        "final"
                    ]);
                    string final = RequireString(properties, "final");
                    int finalBytes = StrictUtf8.GetByteCount(final);
                    if (finalBytes == 0
                        || finalBytes > _route.MaximumReplyUtf8Bytes) {
                        throw new InvalidDataException(
                            "Sidecar completed final violates its UTF-8 bound."
                        );
                    }
                    generation.Complete(
                        RequireWireIdentifier(properties, "dispatchId"),
                        RequireWireIdentifier(properties, "threadId"),
                        RequireWireIdentifier(properties, "turnId"),
                        final
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
        RequireAllowedKeys(properties, [
            "v", "type", "requestId", "dispatchId", "threadId",
            "turnId", "stage", "code"
        ]);
        RequirePresent(properties, ["v", "type", "stage", "code"]);
        string stage = RequireString(properties, "stage");
        if (stage is not ("protocol" or "start" or "turn" or "shutdown")) {
            throw new InvalidDataException("Sidecar failure stage is invalid.");
        }
        string code = RequireWireIdentifier(properties, "code");
        string? requestId = OptionalWireIdentifier(properties, "requestId");
        string? dispatchId = OptionalWireIdentifier(properties, "dispatchId");
        string? threadId = OptionalWireIdentifier(properties, "threadId");
        string? turnId = OptionalWireIdentifier(properties, "turnId");
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
    }

    private static Dictionary<string, JsonElement> ReadStrictProperties(
        JsonElement root
    ) {
        if (root.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException("Sidecar frame must be an object.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject()) {
            if (!seen.Add(property.Name)) {
                throw new InvalidDataException(
                    "Sidecar frame contains duplicate properties."
                );
            }
            result.Add(property.Name, property.Value);
        }
        return result;
    }

    private static void RequireProtocolVersion(
        Dictionary<string, JsonElement> properties
    ) {
        if (!properties.TryGetValue("v", out JsonElement version)
            || !version.TryGetInt32(out int value)
            || value != ProtocolVersion) {
            throw new InvalidDataException(
                "Sidecar frame has an unsupported protocol version."
            );
        }
    }

    private static string RequireString(
        Dictionary<string, JsonElement> properties,
        string name
    ) {
        if (!properties.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException(
                $"Sidecar frame requires string '{name}'."
            );
        }
        return element.GetString()
            ?? throw new InvalidDataException(
                $"Sidecar frame string '{name}' is null."
            );
    }

    private static string RequireWireIdentifier(
        Dictionary<string, JsonElement> properties,
        string name
    ) {
        string value = RequireString(properties, name);
        try {
            RequireIdentifier(value, name);
            return value;
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"Sidecar frame identifier '{name}' is invalid.",
                exception
            );
        }
    }

    private static string? OptionalWireIdentifier(
        Dictionary<string, JsonElement> properties,
        string name
    ) => properties.ContainsKey(name)
        ? RequireWireIdentifier(properties, name)
        : null;

    private static void RequireExactKeys(
        Dictionary<string, JsonElement> properties,
        IReadOnlyList<string> expected
    ) {
        if (properties.Count != expected.Count
            || expected.Any(key => !properties.ContainsKey(key))) {
            throw new InvalidDataException(
                "Sidecar frame has missing or unknown properties."
            );
        }
    }

    private static void RequireAllowedKeys(
        Dictionary<string, JsonElement> properties,
        IReadOnlyList<string> allowed
    ) {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        if (properties.Keys.Any(key => !set.Contains(key))) {
            throw new InvalidDataException(
                "Sidecar frame has an unknown property."
            );
        }
    }

    private static void RequirePresent(
        Dictionary<string, JsonElement> properties,
        IReadOnlyList<string> required
    ) {
        if (required.Any(key => !properties.ContainsKey(key))) {
            throw new InvalidDataException(
                "Sidecar frame is missing a required property."
            );
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    private sealed class Generation {
        private readonly object _pendingGate = new();
        private readonly GalateaCodexSidecarClient _owner;
        private readonly Process _process;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Dictionary<string, PendingDispatch> _byRequest =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingDispatch> _byDispatch =
            new(StringComparer.Ordinal);
        private int _failed;
        private int _ready;

        internal Generation(
            GalateaCodexSidecarClient owner,
            int id,
            ProcessStartInfo startInfo
        ) {
            _owner = owner;
            Id = id;
            _process = new Process {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
        }

        internal int Id { get; }
        internal TaskCompletionSource Ready { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal Task CleanupTask { get; set; } = Task.CompletedTask;
        internal bool IsFailed => Volatile.Read(ref _failed) != 0;
        internal bool IsReady => Volatile.Read(ref _ready) != 0;

        internal void Start() {
            try {
                if (!_process.Start()) {
                    throw new InvalidOperationException(
                        "Galatea delegate sidecar process did not start."
                    );
                }
            }
            catch {
                _process.Dispose();
                throw;
            }
            Task stdout = ReadStdoutAsync();
            _ = DrainStderrAsync();
            _ = ObserveExitAsync(stdout);
            DebugUtil.Info(
                LogCategory,
                $"Sidecar process started: generation={Id}."
            );
        }

        internal async Task<GalateaDelegateAcceptedHandle> DispatchAsync(
            GalateaDelegateDispatchRequest request,
            CancellationToken ct
        ) {
            string requestId = Guid.NewGuid().ToString("N");
            PendingDispatch pending;
            bool ownsWrite;
            lock (_pendingGate) {
                if (Volatile.Read(ref _failed) != 0) {
                    throw new GalateaDelegateStartException(
                        "protocol",
                        "SIDECAR_UNAVAILABLE"
                    );
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
                return await pending.Accepted.Task.WaitAsync(
                        TimeSpan.FromMilliseconds(
                            _owner._config.Sidecar.RpcTimeoutMs
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
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
            if (frame.Length > _owner._config.Sidecar.MaximumFrameUtf8Bytes) {
                RemovePending(pending);
                throw new ArgumentException(
                    "Encoded delegate dispatch exceeds its frame limit.",
                    nameof(request)
                );
            }
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try {
                await _process.StandardInput.BaseStream.WriteAsync(
                    frame,
                    ct
                ).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.WriteAsync(
                    "\n"u8.ToArray(),
                    ct
                ).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.FlushAsync(ct)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or ObjectDisposedException
                    or InvalidOperationException) {
                _owner.FailGeneration(
                    this,
                    "protocol",
                    "SIDECAR_WRITE_FAILED",
                    graceful: false
                );
                throw new GalateaDelegateStartException(
                    "protocol",
                    "SIDECAR_WRITE_FAILED"
                );
            }
            finally {
                _writeGate.Release();
            }
            return await pending.Accepted.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(
                        _owner._config.Sidecar.RpcTimeoutMs
                    ),
                    ct
                )
                .ConfigureAwait(false);
        }

        internal bool TrySetReady() {
            if (Interlocked.Exchange(ref _ready, 1) != 0
                || Volatile.Read(ref _failed) != 0) {
                return false;
            }
            Ready.TrySetResult();
            return true;
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

        internal bool TryMarkFailure() =>
            Interlocked.Exchange(ref _failed, 1) == 0;

        internal void CompleteFailure(string stage, string code) {
            Ready.TrySetException(
                new GalateaDelegateStartException(stage, code)
            );
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

        internal async Task TerminateAsync(bool graceful) {
            try {
                if (graceful && !_process.HasExited) {
                    try {
                        _process.StandardInput.Close();
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or InvalidOperationException
                            or ObjectDisposedException) { }
                    if (await WaitForExitBoundedAsync().ConfigureAwait(false)) {
                        return;
                    }
                }
                if (!_process.HasExited) {
                    try {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) { }
                }
                _ = await WaitForExitBoundedAsync().ConfigureAwait(false);
            }
            finally {
                _process.Dispose();
            }
        }

        private async Task<bool> WaitForExitBoundedAsync() {
            try {
                await _process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromMilliseconds(
                            _owner._config.Sidecar.ShutdownGraceMs
                        )
                    )
                    .ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) {
                return false;
            }
        }

        private async Task ReadStdoutAsync() {
            var line = new ArrayBufferWriter<byte>();
            byte[] buffer = GC.AllocateUninitializedArray<byte>(4096);
            try {
                while (true) {
                    int read = await _process.StandardOutput.BaseStream
                        .ReadAsync(buffer)
                        .ConfigureAwait(false);
                    if (read == 0) {
                        if (line.WrittenCount != 0) {
                            _owner.FailGeneration(
                                this,
                                "protocol",
                                "SIDECAR_PROTOCOL_ERROR",
                                graceful: false
                            );
                        }
                        return;
                    }
                    int start = 0;
                    for (int index = 0; index < read; index++) {
                        if (buffer[index] != (byte)'\n') {
                            continue;
                        }
                        Append(line, buffer.AsSpan(start, index - start));
                        if (line.WrittenCount == 0) {
                            _owner.FailGeneration(
                                this,
                                "protocol",
                                "SIDECAR_PROTOCOL_ERROR",
                                graceful: false
                            );
                            return;
                        }
                        _owner.ProcessFrame(this, line.WrittenSpan.ToArray());
                        line.Clear();
                        if (Volatile.Read(ref _failed) != 0) {
                            return;
                        }
                        start = index + 1;
                    }
                    Append(line, buffer.AsSpan(start, read - start));
                    if (line.WrittenCount
                        > _owner._config.Sidecar.MaximumFrameUtf8Bytes) {
                        _owner.FailGeneration(
                            this,
                            "protocol",
                            "SIDECAR_FRAME_TOO_LARGE",
                            graceful: false
                        );
                        return;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or ObjectDisposedException
                    or InvalidOperationException) {
                _owner.FailGeneration(
                    this,
                    "protocol",
                    "SIDECAR_READ_FAILED",
                    graceful: false
                );
            }
        }

        private async Task DrainStderrAsync() {
            byte[] buffer = GC.AllocateUninitializedArray<byte>(4096);
            long total = 0;
            try {
                while (true) {
                    int read = await _process.StandardError.BaseStream
                        .ReadAsync(buffer)
                        .ConfigureAwait(false);
                    if (read == 0) {
                        break;
                    }
                    total = Math.Min(long.MaxValue - read, total) + read;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or ObjectDisposedException
                    or InvalidOperationException) { }
            DebugUtil.Trace(
                LogCategory,
                $"Sidecar stderr drained: generation={Id}, bytes={total}."
            );
        }

        private async Task ObserveExitAsync(Task stdout) {
            try {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or ObjectDisposedException) { }
            try {
                await stdout.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) { }
            _owner.FailGeneration(
                this,
                "protocol",
                "SIDECAR_EXITED",
                graceful: false
            );
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

        private void RemovePending(PendingDispatch pending) {
            lock (_pendingGate) {
                _byRequest.Remove(pending.RequestId);
                _byDispatch.Remove(pending.DispatchId);
            }
        }

        private static void Append(
            ArrayBufferWriter<byte> target,
            ReadOnlySpan<byte> bytes
        ) {
            if (bytes.IsEmpty) {
                return;
            }
            bytes.CopyTo(target.GetSpan(bytes.Length));
            target.Advance(bytes.Length);
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
}
