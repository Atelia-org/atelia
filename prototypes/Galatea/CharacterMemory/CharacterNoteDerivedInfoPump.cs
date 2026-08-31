using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Atelia.Diagnostics;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>
/// Owns the bounded, session-lifetime background dispatch of durable
/// Character Note DerivedInfo work. Signals coalesce and each consumed signal
/// attempts at most one durable work item.
/// </summary>
internal sealed class CharacterNoteDerivedInfoPump : IAsyncDisposable {
    internal static readonly TimeSpan DefaultProviderDeadline =
        TimeSpan.FromSeconds(30);

    private readonly CharacterNoteDefaultPodReconciler _reconciler;
    private readonly ICharacterNoteDerivedInfoEnricher _enricher;
    private readonly SessionJournalEngine _engine;
    private readonly SemaphoreSlim _turnLock;
    private readonly TimeSpan _providerDeadline;
    private readonly Channel<bool> _signals;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _signalGate = new();
    private readonly Task _runTask;
    private long _publishedSignalGeneration;
    private long _consumedSignalGeneration;
    private int _disposeStarted;

    internal CharacterNoteDerivedInfoPump(
        CharacterNoteDefaultPodReconciler reconciler,
        ICharacterNoteDerivedInfoEnricher enricher,
        SessionJournalEngine engine,
        SemaphoreSlim turnLock,
        TimeSpan? providerDeadline = null
    ) {
        _reconciler = reconciler
            ?? throw new ArgumentNullException(nameof(reconciler));
        _enricher = enricher
            ?? throw new ArgumentNullException(nameof(enricher));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _turnLock = turnLock
            ?? throw new ArgumentNullException(nameof(turnLock));
        _providerDeadline = providerDeadline
            ?? DefaultProviderDeadline;
        if (_providerDeadline <= TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(
                nameof(providerDeadline),
                "Character Note DerivedInfo provider deadline must be positive."
            );
        }

        _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _runTask = RunAsync();
    }

    internal bool Signal() {
        lock (_signalGate) {
            if (_disposeStarted != 0) { return false; }
            _publishedSignalGeneration = checked(
                _publishedSignalGeneration + 1
            );
            _ = _signals.Writer.TryWrite(true);
            return true;
        }
    }

    private async Task RunAsync() {
        try {
            await foreach (bool wakeup in _signals.Reader.ReadAllAsync(
                    _lifetime.Token)) {
                _ = wakeup;
                while (_signals.Reader.TryRead(out _)) { }
                while (TryConsumeSignalGeneration()) {
                    await ReconcileOneAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested) {
        }
    }

    private bool TryConsumeSignalGeneration() {
        lock (_signalGate) {
            if (_consumedSignalGeneration
                    >= _publishedSignalGeneration) {
                return false;
            }
            _consumedSignalGeneration++;
            return true;
        }
    }

    private async Task ReconcileOneAsync() {
        long started = Stopwatch.GetTimestamp();
        try {
            CharacterNoteDerivedInfoReconcileResult result =
                await _reconciler.ReconcileNextDerivedInfoAsync(
                        MaterializeAsync,
                        _enricher,
                        _lifetime.Token,
                        _providerDeadline
                    )
                    .ConfigureAwait(false);
            LogOutcome(result, ElapsedMilliseconds(started));
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested) {
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            DebugUtil.Warning(
                "Galatea.CharacterMemory",
                "Character Note DerivedInfo pump failed: "
                    + "source=none outcome=failed "
                    + $"code={exception.GetType().Name} "
                    + $"elapsedMs={ElapsedMilliseconds(started)}"
            );
        }
    }

    private async ValueTask<CharacterNoteDerivedInfoEnrichmentRequest>
        MaterializeAsync(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        CancellationToken cancellationToken
    ) {
        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            return CharacterNoteDerivedInfoContextMaterializer.Materialize(
                _engine,
                work,
                cancellationToken
            );
        }
        finally {
            _turnLock.Release();
        }
    }

    private static void LogOutcome(
        CharacterNoteDerivedInfoReconcileResult result,
        long elapsedMilliseconds
    ) {
        (string source, string outcome, string code, DebugEventKind kind) =
            result switch {
                CharacterNoteDerivedInfoReconcileResult.NoWork =>
                    ("none", "no-work", "none", DebugEventKind.Skip),
                CharacterNoteDerivedInfoReconcileResult.Applied applied =>
                    (Format(applied.SourceAction), "applied", "none",
                        DebugEventKind.Success),
                CharacterNoteDerivedInfoReconcileResult.Rejected rejected =>
                    (Format(rejected.SourceAction), "rejected",
                        rejected.Code, DebugEventKind.Skip),
                CharacterNoteDerivedInfoReconcileResult.Deferred deferred =>
                    (Format(deferred.SourceAction), "deferred",
                        deferred.Code, DebugEventKind.Failure),
                CharacterNoteDerivedInfoReconcileResult.Quarantined
                        quarantined =>
                    ("none", "quarantined", quarantined.Code,
                        DebugEventKind.Failure),
                _ => throw new InvalidDataException(
                    "Unknown Character Note DerivedInfo reconciliation result."
                ),
            };
        DebugUtil.Info(
            "Galatea.CharacterMemory",
            "Character Note DerivedInfo pump completed: "
                + $"source={source} outcome={outcome} code={code} "
                + $"elapsedMs={elapsedMilliseconds}",
            eventKind: kind
        );
    }

    private static string Format(EventAddress address) =>
        EventAddressTextCodec.Format(address);

    private static long ElapsedMilliseconds(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public async ValueTask DisposeAsync() {
        bool firstDisposal;
        lock (_signalGate) {
            firstDisposal = _disposeStarted == 0;
            if (firstDisposal) {
                _disposeStarted = 1;
                _signals.Writer.TryComplete();
            }
        }
        if (!firstDisposal) {
            await _runTask.ConfigureAwait(false);
            return;
        }

        var failures = new List<Exception>(3);
        try {
            _lifetime.Cancel();
        }
        catch (Exception exception) {
            failures.Add(exception);
        }
        try {
            await _runTask.ConfigureAwait(false);
        }
        catch (Exception exception) {
            failures.Add(exception);
        }
        try {
            _lifetime.Dispose();
        }
        catch (Exception exception) {
            failures.Add(exception);
        }
        ThrowDisposeFailures(failures);
    }

    private static void ThrowDisposeFailures(
        IReadOnlyList<Exception> failures
    ) {
        if (failures.Count == 0) { return; }
        if (failures.Count == 1) {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        Exception? fatal = failures.FirstOrDefault(static exception =>
            !GalateaExceptionClassifier.IsNonFatal(exception));
        if (fatal is not null) {
            throw new AggregateException(
                "Character Note DerivedInfo pump disposal encountered a fatal failure.",
                failures
            );
        }
        throw new AggregateException(
            "Character Note DerivedInfo pump disposal encountered multiple failures.",
            failures
        );
    }
}
