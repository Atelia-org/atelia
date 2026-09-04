using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Atelia.Diagnostics;

namespace Atelia.Galatea.Server;

internal abstract class GalateaSidecarOperationException : Exception {
    protected GalateaSidecarOperationException(
        string message,
        string stage,
        string code
    ) : base(message) {
        Stage = stage;
        Code = code;
    }

    internal string Stage { get; }
    internal string Code { get; }
}

internal sealed record GalateaSidecarProcessTestHooks(
    Func<Process, TimeSpan, Task<bool>>? WaitForExitBoundedAsync = null
);

/// <summary>
/// Owns only the lazy child-process generation, bounded JSONL transport and
/// restart/reap lifecycle used by the exact durable transport. Business
/// correlation and state machines remain outside the process transport.
/// </summary>
internal abstract class GalateaSidecarProcessClientBase : IAsyncDisposable {
    private const int ReadyStartupMarginMs = 5_000;
    private const int SidecarOutputWriteTimeoutMs = 10_000;
    internal const string LogCategory = "Galatea.DelegateSidecar";

    private static readonly HashSet<string> ParentCodexContextKeys = new(
        StringComparer.Ordinal
    ) {
        "CODEX_SESSION_ID",
        "CODEX_THREAD_ID",
        "CODEX_INTERNAL_ORIGINATOR_OVERRIDE",
        "CODEX_PERMISSION_PROFILE",
        "CODEX_CI"
    };

    private readonly object _stateGate = new();
    private readonly GalateaSidecarProcessTestHooks? _processHooks;
    private GalateaSidecarProcessGeneration? _generation;
    private Task _restartBarrier = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _disposed;
    private int _nextGeneration;

    protected GalateaSidecarProcessClientBase(
        GalateaDelegateConfig config,
        GalateaSidecarProcessTestHooks? processHooks = null
    ) {
        Config = GalateaDelegateConfigReader.Validate(config);
        Route = Config.CodexRoute;
        _processHooks = processHooks;
    }

    protected GalateaDelegateConfig Config { get; }
    protected GalateaDelegateRouteConfig Route { get; }

    protected bool HasStartedProcess {
        get {
            lock (_stateGate) {
                return _nextGeneration != 0;
            }
        }
    }

    protected int GenerationCount {
        get {
            lock (_stateGate) {
                return _nextGeneration;
            }
        }
    }

    protected async Task<GalateaSidecarProcessGeneration>
        GetReadyGenerationAsync(CancellationToken ct) {
        while (true) {
            GalateaSidecarProcessGeneration? generation;
            Task barrier;
            lock (_stateGate) {
                ObjectDisposedException.ThrowIf(_disposed, this);
                generation = _generation;
                barrier = _restartBarrier;
                if (generation is null
                    && barrier.Status == TaskStatus.RanToCompletion) {
                    generation = CreateGeneration(
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
                await AwaitRestartBarrierAsync(barrier, ct)
                    .ConfigureAwait(false);
                continue;
            }
            try {
                await generation.Ready.Task.WaitAsync(
                        ComputeReadyDeadline(Config.Sidecar.RpcTimeoutMs),
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
                await generation.CleanupTask.ConfigureAwait(false);
                throw CreateFailureException(
                    "protocol",
                    "SIDECAR_READY_TIMEOUT"
                );
            }
        }
    }

    protected void FailGeneration(
        GalateaSidecarProcessGeneration generation,
        string stage,
        string code,
        bool graceful
    ) {
        Task cleanup;
        lock (_stateGate) {
            if (!generation.TryMarkFailure(stage, code)) {
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

    public ValueTask DisposeAsync() {
        lock (_stateGate) {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    protected static TimeSpan ComputeReadyDeadline(int rpcTimeoutMs) {
        if (rpcTimeoutMs is < 100 or > 300_000) {
            throw new ArgumentOutOfRangeException(nameof(rpcTimeoutMs));
        }
        long milliseconds = checked(2L * rpcTimeoutMs + ReadyStartupMarginMs);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    protected ProcessStartInfo CreateStartInfo() {
        GalateaDelegateSidecarConfig sidecar = Config.Sidecar;
        var startInfo = new ProcessStartInfo {
            FileName = sidecar.NodeCommand,
            WorkingDirectory = Route.Cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(sidecar.EntryPoint);
        ConfigureSidecarEnvironment(startInfo.Environment);
        return startInfo;
    }

    protected void ConfigureSidecarEnvironment(
        IDictionary<string, string?> environment
    ) {
        GalateaDelegateSidecarConfig sidecar = Config.Sidecar;
        foreach (string inherited in environment.Keys
                     .Where(static key =>
                         key.StartsWith(
                             "CODEX_BRIDGE_",
                             StringComparison.Ordinal
                         )
                         || key.StartsWith(
                             "GALATEA_CODEX_",
                             StringComparison.Ordinal
                         )
                         || ParentCodexContextKeys.Contains(key))
                     .ToArray()) {
            environment.Remove(inherited);
        }
        environment["CODEX_BRIDGE_TRANSPORT"] = "stdio";
        environment["CODEX_BRIDGE_HTTP_HOST"] = "127.0.0.1";
        environment["CODEX_BRIDGE_HTTP_PORT"] = "3000";
        environment["CODEX_BRIDGE_ALLOW_INSECURE_HTTP"] = "false";
        environment["CODEX_BRIDGE_ALLOWED_ROOTS"] =
            JsonSerializer.Serialize(Config.AllowedRoots);
        environment["CODEX_BRIDGE_DEFAULT_CWD"] = Route.Cwd;
        environment["CODEX_BRIDGE_CODEX_COMMAND"] = sidecar.CodexCommand;
        environment["CODEX_BRIDGE_CODEX_ARGS"] =
            "[\"app-server\",\"--listen\",\"stdio://\",\"-c\","
            + "\"mcp_servers={}\",\"-c\",\"features.apps=false\"]";
        environment["CODEX_BRIDGE_DEFAULT_WAIT_MS"] = "0";
        environment["CODEX_BRIDGE_MAX_WAIT_MS"] = "60000";
        environment["CODEX_BRIDGE_RPC_TIMEOUT_MS"] =
            sidecar.RpcTimeoutMs.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        environment["CODEX_BRIDGE_MAX_RESULT_CHARS"] = "12000";
        environment["CODEX_BRIDGE_MAX_PROGRESS_CHARS"] = "2000";
        environment["CODEX_BRIDGE_VERBOSE"] = "false";
        environment["GALATEA_CODEX_MODE"] =
            Route.Mode == GalateaDelegateMode.Work ? "work" : "research";
        environment["GALATEA_CODEX_LOCAL_COMMAND_NETWORK"] =
            Route.LocalCommandNetwork ? "true" : "false";
        environment["GALATEA_CODEX_WEB_SEARCH"] =
            Route.Tools.WebSearch switch {
                GalateaDelegateWebSearchMode.Disabled => "disabled",
                GalateaDelegateWebSearchMode.Cached => "cached",
                GalateaDelegateWebSearchMode.Indexed => "indexed",
                GalateaDelegateWebSearchMode.Live => "live",
                _ => throw new InvalidOperationException(
                    "Galatea delegate web-search mode is invalid."
                )
            };
        environment["GALATEA_CODEX_IMAGE_GENERATION"] =
            Route.Tools.ImageGeneration ? "true" : "false";
        environment["GALATEA_CODEX_VIEW_IMAGE"] =
            Route.Tools.ViewImage ? "true" : "false";
        environment["GALATEA_CODEX_MAX_INPUT_FRAME_BYTES"] =
            sidecar.MaximumFrameUtf8Bytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        environment["GALATEA_CODEX_MAX_OUTPUT_FRAME_BYTES"] =
            sidecar.MaximumFrameUtf8Bytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        environment["GALATEA_CODEX_MAX_TASK_BYTES"] =
            Route.MaximumTaskUtf8Bytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        environment["GALATEA_CODEX_MAX_FINAL_BYTES"] =
            Route.MaximumReplyUtf8Bytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        environment["GALATEA_CODEX_OUTPUT_WRITE_TIMEOUT_MS"] =
            SidecarOutputWriteTimeoutMs.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
    }

    protected abstract GalateaSidecarProcessGeneration CreateGeneration(
        int id,
        ProcessStartInfo startInfo
    );

    protected abstract Exception CreateFailureException(
        string stage,
        string code
    );

    internal abstract void ProcessFrame(
        GalateaSidecarProcessGeneration generation,
        byte[] line
    );

    private async Task AwaitRestartBarrierAsync(
        Task barrier,
        CancellationToken ct
    ) {
        try {
            await barrier.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (GalateaSidecarOperationException exception) when (
            string.Equals(
                exception.Code,
                "SIDECAR_REAP_UNCONFIRMED",
                StringComparison.Ordinal
            )) {
            throw;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            throw CreateFailureException(
                "shutdown",
                "SIDECAR_REAP_UNCONFIRMED"
            );
        }
    }

    private async Task DisposeCoreAsync() {
        GalateaSidecarProcessGeneration? generation;
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
            await StopGenerationForDisposeAsync(generation)
                .ConfigureAwait(false);
        }
        await barrier.ConfigureAwait(false);
    }

    private async Task StopGenerationForDisposeAsync(
        GalateaSidecarProcessGeneration generation
    ) {
        Task cleanup;
        bool normalStop;
        lock (_stateGate) {
            normalStop = generation.TryMarkFailure(
                "shutdown",
                "SIDECAR_DISPOSED"
            );
            if (normalStop) {
                cleanup = generation.TerminateAsync(graceful: true);
                generation.CleanupTask = cleanup;
            }
            else {
                cleanup = generation.CleanupTask;
            }
        }
        if (!normalStop) {
            await cleanup.ConfigureAwait(false);
            return;
        }

        generation.CompleteFailure("shutdown", "SIDECAR_DISPOSED");
        DebugUtil.Info(
            LogCategory,
            $"Sidecar generation stopping: generation={generation.Id}."
        );
        try {
            await cleanup.ConfigureAwait(false);
            DebugUtil.Info(
                LogCategory,
                $"Sidecar generation stopped: generation={generation.Id}.",
                eventKind: DebugEventKind.Success
            );
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            string stage = exception is GalateaSidecarOperationException sidecar
                ? sidecar.Stage
                : "shutdown";
            string code = exception is GalateaSidecarOperationException failure
                ? failure.Code
                : "SIDECAR_REAP_UNCONFIRMED";
            DebugUtil.Warning(
                LogCategory,
                $"Sidecar generation shutdown failed: generation={generation.Id}, "
                    + $"stage={stage}, code={code}.",
                exception,
                DebugEventKind.Failure
            );
            throw;
        }
    }

    internal Exception NewFailure(string stage, string code) =>
        CreateFailureException(stage, code);

    internal GalateaDelegateConfig ConfigForGeneration => Config;

    internal GalateaSidecarProcessTestHooks? ProcessHooks => _processHooks;

    internal void FailGenerationForGeneration(
        GalateaSidecarProcessGeneration generation,
        string stage,
        string code,
        bool graceful
    ) => FailGeneration(generation, stage, code, graceful);
}

internal abstract class GalateaSidecarProcessGeneration {
    private readonly Process _process;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _failed;
    private int _ready;
    private string _failureStage = "protocol";
    private string _failureCode = "SIDECAR_UNAVAILABLE";

    protected GalateaSidecarProcessGeneration(
        GalateaSidecarProcessClientBase owner,
        int id,
        ProcessStartInfo startInfo
    ) {
        Owner = owner;
        Id = id;
        _process = new Process {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }

    protected GalateaSidecarProcessClientBase Owner { get; }
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
            GalateaSidecarProcessClientBase.LogCategory,
            $"Node sidecar process started: generation={Id}, pid={_process.Id}.",
            eventKind: DebugEventKind.Start
        );
    }

    internal async Task WriteFrameAsync(
        byte[] framed,
        CancellationToken ct,
        Action? beforeWrite = null
    ) {
        ArgumentNullException.ThrowIfNull(framed);
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(
                Owner.ConfigForGeneration.Sidecar.RpcTimeoutMs
            )
        );
        using var linked = CancellationTokenSource
            .CreateLinkedTokenSource(ct, deadline.Token);
        bool gateHeld = false;
        bool writeStarted = false;
        try {
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            gateHeld = true;
            if (IsFailed) {
                throw CurrentFailure();
            }
            beforeWrite?.Invoke();
            writeStarted = true;
            Task write = _process.StandardInput.BaseStream.WriteAsync(
                framed,
                linked.Token
            ).AsTask();
            await AwaitHardBoundedAsync(write, linked.Token)
                .ConfigureAwait(false);
            Task flush = _process.StandardInput.BaseStream.FlushAsync(
                linked.Token
            );
            await AwaitHardBoundedAsync(flush, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!writeStarted) {
            if (ct.IsCancellationRequested) {
                throw;
            }
            throw Owner.NewFailure(
                "protocol",
                "SIDECAR_WRITE_GATE_TIMEOUT"
            );
        }
        catch (OperationCanceledException) when (writeStarted) {
            Owner.FailGenerationForGeneration(
                this,
                "protocol",
                "SIDECAR_WRITE_OUTCOME_UNKNOWN",
                graceful: false
            );
            throw Owner.NewFailure(
                "protocol",
                "SIDECAR_WRITE_OUTCOME_UNKNOWN"
            );
        }
        catch (Exception exception) when (
            exception is IOException
                or ObjectDisposedException
                or InvalidOperationException) {
            Owner.FailGenerationForGeneration(
                this,
                "protocol",
                writeStarted
                    ? "SIDECAR_WRITE_OUTCOME_UNKNOWN"
                    : "SIDECAR_WRITE_FAILED",
                graceful: false
            );
            throw Owner.NewFailure(
                "protocol",
                writeStarted
                    ? "SIDECAR_WRITE_OUTCOME_UNKNOWN"
                    : "SIDECAR_WRITE_FAILED"
            );
        }
        finally {
            if (gateHeld) {
                _writeGate.Release();
            }
        }
    }

    internal bool TrySetReady() {
        if (Interlocked.Exchange(ref _ready, 1) != 0
            || Volatile.Read(ref _failed) != 0) {
            return false;
        }
        Ready.TrySetResult();
        return true;
    }

    internal bool TryMarkFailure(string stage, string code) {
        if (Volatile.Read(ref _failed) != 0) {
            return false;
        }
        _failureStage = stage;
        _failureCode = code;
        Volatile.Write(ref _failed, 1);
        return true;
    }

    internal void CompleteFailure(string stage, string code) {
        Ready.TrySetException(Owner.NewFailure(stage, code));
        CompletePendingFailure(stage, code);
    }

    protected abstract void CompletePendingFailure(string stage, string code);

    internal async Task TerminateAsync(bool graceful) {
        bool reapConfirmed = false;
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
                    reapConfirmed = true;
                    return;
                }
            }
            if (!_process.HasExited) {
                try {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
            }
            if (!await WaitForExitBoundedAsync().ConfigureAwait(false)) {
                throw Owner.NewFailure(
                    "shutdown",
                    "SIDECAR_REAP_UNCONFIRMED"
                );
            }
            reapConfirmed = true;
        }
        catch (GalateaSidecarOperationException) {
            throw;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            throw Owner.NewFailure(
                "shutdown",
                "SIDECAR_REAP_UNCONFIRMED"
            );
        }
        finally {
            if (reapConfirmed) {
                _process.Dispose();
            }
        }
    }

    private async Task<bool> WaitForExitBoundedAsync() {
        TimeSpan deadline = TimeSpan.FromMilliseconds(
            Owner.ConfigForGeneration.Sidecar.ShutdownGraceMs
        );
        if (Owner.ProcessHooks?.WaitForExitBoundedAsync is { } wait) {
            return await wait(_process, deadline).ConfigureAwait(false);
        }
        try {
            await _process.WaitForExitAsync().WaitAsync(deadline)
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
                        Owner.FailGenerationForGeneration(
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
                    if (line.WrittenCount
                        > Owner.ConfigForGeneration.Sidecar
                            .MaximumFrameUtf8Bytes) {
                        Owner.FailGenerationForGeneration(
                            this,
                            "protocol",
                            "SIDECAR_FRAME_TOO_LARGE",
                            graceful: false
                        );
                        return;
                    }
                    if (line.WrittenCount == 0) {
                        Owner.FailGenerationForGeneration(
                            this,
                            "protocol",
                            "SIDECAR_PROTOCOL_ERROR",
                            graceful: false
                        );
                        return;
                    }
                    Owner.ProcessFrame(this, line.WrittenSpan.ToArray());
                    line.Clear();
                    if (Volatile.Read(ref _failed) != 0) {
                        return;
                    }
                    start = index + 1;
                }
                Append(line, buffer.AsSpan(start, read - start));
                if (line.WrittenCount
                    > Owner.ConfigForGeneration.Sidecar
                        .MaximumFrameUtf8Bytes) {
                    Owner.FailGenerationForGeneration(
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
            Owner.FailGenerationForGeneration(
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
            GalateaSidecarProcessClientBase.LogCategory,
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
        Owner.FailGenerationForGeneration(
            this,
            "protocol",
            "SIDECAR_EXITED",
            graceful: false
        );
    }

    protected Exception CurrentFailure() => Owner.NewFailure(
        _failureStage,
        _failureCode
    );

    private static async Task AwaitHardBoundedAsync(
        Task operation,
        CancellationToken ct
    ) {
        try {
            await operation.WaitAsync(ct).ConfigureAwait(false);
        }
        catch {
            _ = operation.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            throw;
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
