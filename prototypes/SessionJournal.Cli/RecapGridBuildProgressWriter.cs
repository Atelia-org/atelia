using System.Diagnostics;
using System.Globalization;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;

namespace Atelia.SessionJournal.Cli;

/// <summary>One build-scoped stderr observer. It never contains prompt or response text.</summary>
internal sealed class RecapGridBuildProgressWriter : IRecapCompletionTelemetry, IAsyncDisposable {
    private readonly object _gate = new();
    private readonly TextWriter _stderr;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _waiting;
    private readonly Dictionary<CellSlot, Pending> _pending = [];
    private int _started;
    private int _ended;
    private int _rows;
    private int _existingRows;
    private bool _stopped;
    private bool _finished;
    private bool _disposed;

    internal RecapGridBuildProgressWriter(TextWriter stderr, TimeSpan? waitInterval = null) {
        ArgumentNullException.ThrowIfNull(stderr);
        TimeSpan interval = waitInterval ?? TimeSpan.FromSeconds(30);
        if (interval <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(waitInterval)); }
        _stderr = stderr;
        _waiting = WaitAsync(interval);
    }

    public void Record(RecapCompletionTelemetryEvent value) {
        lock (_gate) {
            if (_stopped) { return; }
            if (value.Kind == "completion-started") {
                var pending = new Pending(++_started, Stopwatch.GetTimestamp());
                _pending.Add(value.Slot, pending);
                Write($"request-start request={pending.Number} row={value.Slot.HistoryRowId.Value} column={Safe(value.Slot.LogicalColumnId.Value)} connection={Safe(value.ConnectionId)} model={Safe(value.ModelId)} boundary=local-invoker");
            }
            else if (value.Kind == "completion-settled" && _pending.Remove(value.Slot, out Pending? pending)) {
                _ended++;
                Write($"request-end request={pending.Number} elapsed-seconds={Seconds(pending)} outcome={Safe(value.ProviderOutcome)} code={Safe(value.Code ?? "none")}");
            }
        }
    }

    internal void RecordRowCommitted(RecapGridRowCommitProgress progress) {
        lock (_gate) {
            if (_stopped) { return; }
            if (progress.AlreadyPresent) { _existingRows++; } else { _rows++; }
            Write($"{(progress.AlreadyPresent ? "row-existing" : "row-committed")} recipe={progress.RecipeDigest.Value} row={progress.RowId.Value} result={progress.RowResultId.Value}");
        }
    }

    internal async ValueTask FinishAsync(RecapGridBuildResult result) {
        await StopAsync().ConfigureAwait(false);
        lock (_gate) {
            if (_finished) { return; }
            _finished = true;
            RecapGridBuildMetrics metrics = result.Metrics;
            Write($"summary status={result.GetType().Name} requests-started={_started} requests-ended={_ended} requests-pending={_pending.Count} rows-confirmed={_rows} rows-existing={_existingRows} new-calls={metrics.NewCalls} cells-committed={metrics.CellsCommitted} row-views-committed={metrics.RowViewsCommitted}");
        }
    }

    public ValueTask DisposeAsync() => StopAsync();

    private async ValueTask StopAsync() {
        lock (_gate) {
            if (!_stopped) { _stopped = true; _stop.Cancel(); }
        }
        await _waiting.ConfigureAwait(false);
        lock (_gate) {
            if (!_disposed) { _disposed = true; _stop.Dispose(); }
        }
    }

    private async Task WaitAsync(TimeSpan interval) {
        using var timer = new PeriodicTimer(interval);
        try {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) {
                lock (_gate) {
                    if (_stopped) { return; }
                    foreach (Pending pending in _pending.Values) {
                        Write($"waiting request={pending.Number} elapsed-seconds={Seconds(pending)}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Write(string message) {
        try {
            _stderr.WriteLine("[recap-build] " + message);
            _stderr.Flush();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException) {
            // A closed stderr must not change build or settlement semantics.
        }
    }

    private static string Seconds(Pending pending) => Stopwatch.GetElapsedTime(pending.Timestamp)
        .TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Safe(string text) => new(text.Take(128)
        .Select(character => char.IsControl(character) || char.IsWhiteSpace(character) ? '_' : character).ToArray());

    private sealed record Pending(int Number, long Timestamp);
}
