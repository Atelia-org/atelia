namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal sealed class RuntimeLifetime {
    private readonly object _gate = new();
    private readonly Func<ValueTask> _onDrainedAsync;
    private readonly AsyncLocal<int> _operationScopeDepth = new();
    private readonly TaskCompletionSource _drained = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _activeOperations;
    private bool _closing;
    private bool _drainCallbackClaimed;

    internal RuntimeLifetime(Func<ValueTask> onDrainedAsync)
        => _onDrainedAsync = onDrainedAsync;

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
        bool startDrain;
        lock (_gate) {
            _closing = true;
            startDrain = TryClaimDrainUnderLock();
        }
        if (startDrain) {
            _ = InvokeDrainCallbackAsync();
        }
        return _drained.Task;
    }

    private void Exit() {
        bool startDrain;
        lock (_gate) {
            _activeOperations--;
            startDrain = TryClaimDrainUnderLock();
        }
        if (startDrain) {
            _ = InvokeDrainCallbackAsync();
        }
    }

    private bool TryClaimDrainUnderLock() {
        if (!_closing
            || _activeOperations != 0
            || _drainCallbackClaimed) {
            return false;
        }
        _drainCallbackClaimed = true;
        return true;
    }

    private async Task InvokeDrainCallbackAsync() {
        using OperationScope scope = EnterOperationScope();
        try {
            await _onDrainedAsync().ConfigureAwait(false);
            _drained.TrySetResult();
        }
        catch (Exception exception) {
            _drained.TrySetException(exception);
        }
    }

    private void ExitOperationScope() => _operationScopeDepth.Value--;

    internal sealed class OperationLease : IDisposable {
        private RuntimeLifetime? _owner;

        internal OperationLease(RuntimeLifetime owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }

    internal sealed class OperationScope : IDisposable {
        private RuntimeLifetime? _owner;

        internal OperationScope(RuntimeLifetime owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)
            ?.ExitOperationScope();
    }
}
