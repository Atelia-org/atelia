namespace Atelia.SessionJournal.RecapGrid.Online;

internal sealed class OnlineLifetime {
    private readonly object _gate = new();
    private readonly Func<ValueTask> _onDrainedAsync;
    private readonly AsyncLocal<int> _operationScopeDepth = new();
    private readonly TaskCompletionSource _drained = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _activeOperations;
    private bool _closing;
    private bool _drainClaimed;

    internal OnlineLifetime(Func<ValueTask> onDrainedAsync)
        => _onDrainedAsync = onDrainedAsync
            ?? throw new ArgumentNullException(nameof(onDrainedAsync));

    internal bool TryEnter(out OperationLease? lease) {
        lock (_gate) {
            if (_closing) {
                lease = null;
                return false;
            }
            _activeOperations++;
            lease = new OperationLease(this);
            return true;
        }
    }

    internal OperationScope EnterOperationScope() {
        _operationScopeDepth.Value++;
        return new OperationScope(this);
    }

    internal void DisposeAndDrain() {
        Task drain = BeginDrain();
        if (_operationScopeDepth.Value == 0) {
            drain.GetAwaiter().GetResult();
        }
    }

    internal ValueTask DisposeAndDrainAsync() {
        Task drain = BeginDrain();
        return _operationScopeDepth.Value == 0
            ? new ValueTask(drain)
            : ValueTask.CompletedTask;
    }

    private Task BeginDrain() {
        bool invoke;
        lock (_gate) {
            _closing = true;
            invoke = TryClaimDrainUnderLock();
        }
        if (invoke) {
            _ = DrainAsync();
        }
        return _drained.Task;
    }

    private void Exit() {
        bool invoke;
        lock (_gate) {
            _activeOperations--;
            invoke = TryClaimDrainUnderLock();
        }
        if (invoke) {
            _ = DrainAsync();
        }
    }

    private bool TryClaimDrainUnderLock() {
        if (!_closing || _activeOperations != 0 || _drainClaimed) {
            return false;
        }
        _drainClaimed = true;
        return true;
    }

    private async Task DrainAsync() {
        using OperationScope scope = EnterOperationScope();
        try {
            await _onDrainedAsync().ConfigureAwait(false);
            _drained.TrySetResult();
        }
        catch (Exception exception) {
            _drained.TrySetException(exception);
            // A reentrant disposer is allowed to initiate drain without
            // awaiting it. Observe the stored fault here to prevent an
            // unobserved-task escalation; later Dispose/DisposeAsync calls
            // still await the same task and rethrow the exact exception.
            _ = _drained.Task.Exception;
        }
    }

    private void ExitOperationScope() => _operationScopeDepth.Value--;

    internal sealed class OperationLease : IDisposable {
        private OnlineLifetime? _owner;
        internal OperationLease(OnlineLifetime owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }

    internal sealed class OperationScope : IDisposable {
        private OnlineLifetime? _owner;
        internal OperationScope(OnlineLifetime owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)
            ?.ExitOperationScope();
    }
}
