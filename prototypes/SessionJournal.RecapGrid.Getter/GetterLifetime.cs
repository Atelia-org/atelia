using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;
using System.Security.Cryptography;

namespace Atelia.SessionJournal.RecapGrid.Getter;

internal sealed class GetterLifetime : IDisposable {
    private readonly object _gate = new();
    private static readonly AsyncLocal<GetterLifetime?> CallbackOwner = new();
    private readonly HistoryTimelineBuildReadSession _timeline;
    private readonly RecapGridCadenceReaderHandle _cadence;
    private readonly RecapGridControlReaderHandle _control;
    private readonly string _repositoryPath;
    private readonly string _ownerNonce = Convert.ToHexStringLower(
        RandomNumberGenerator.GetBytes(16)
    );
    private RecapGridStoreReaderHandle? _store;
    private bool _closing;
    private bool _disposed;
    private int _operations;

    internal GetterLifetime(
        string repositoryPath,
        HistoryTimelineBuildReadSession timeline,
        RecapGridCadenceReaderHandle cadence,
        RecapGridControlReaderHandle control
    ) {
        _repositoryPath = repositoryPath;
        _timeline = timeline;
        _cadence = cadence;
        _control = control;
    }

    internal HistoryTimelineBuildReadSession TimelineSession => _timeline;
    internal HistoryTimelineReader Timeline => _timeline.Reader;
    internal RecapGridCadenceReader Cadence => _cadence.Reader;
    internal RecapGridControlReader Control => _control.Reader;
    internal string OwnerNonce => _ownerNonce;

    internal Operation? TryEnter() {
        lock (_gate) {
            if (_closing) {
                return null;
            }
            _operations = checked(_operations + 1);
            GetterLifetime? previous = CallbackOwner.Value;
            CallbackOwner.Value = this;
            return new Operation(this, previous);
        }
    }

    internal StoreOpen OpenStore() {
        lock (_gate) {
            // Closing stops new public operations, but an operation that
            // entered before Dispose must be allowed to finish against the
            // same owned Store handle before the lifetime drain completes.
            if (_disposed) {
                return new StoreOpen.Disposed();
            }
            if (_store is not null) {
                return new StoreOpen.Opened(_store);
            }
            RecapGridStoreReaderOpenResult opened =
                RecapGridStoreFactory.OpenReader(_repositoryPath);
            switch (opened) {
                case RecapGridStoreReaderOpenResult.Opened available:
                    _store = available.Handle;
                    return new StoreOpen.Opened(_store);
                case RecapGridStoreReaderOpenResult.Absent:
                    return new StoreOpen.Absent();
                case RecapGridStoreReaderOpenResult.Busy:
                    return new StoreOpen.Busy();
                case RecapGridStoreReaderOpenResult.UnsupportedSchema schema:
                    return new StoreOpen.UnsupportedSchema(
                        schema.SchemaVersion
                    );
                case RecapGridStoreReaderOpenResult.PlatformUnsupported:
                    return new StoreOpen.Invalid(
                        "RecapGridStorePlatformUnsupported",
                        "The RecapGrid Store platform is unsupported."
                    );
                case RecapGridStoreReaderOpenResult.Invalid invalid:
                    return new StoreOpen.Invalid(
                        invalid.Code,
                        invalid.Detail
                    );
                default:
                    return new StoreOpen.Invalid(
                        "RecapGridStoreOpenOutcomeInvalid",
                        "The RecapGrid Store returned an unknown open outcome."
                    );
            }
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (ReferenceEquals(CallbackOwner.Value, this)) {
                _closing = true;
                return;
            }
            if (_closing) {
                while (!_disposed) {
                    Monitor.Wait(_gate);
                }
                return;
            }
            _closing = true;
            while (_operations != 0) {
                Monitor.Wait(_gate);
            }
            DisposeOwned();
        }
    }

    private void Exit(GetterLifetime? previous) {
        CallbackOwner.Value = previous;
        lock (_gate) {
            _operations--;
            if (_operations == 0) {
                if (_closing && !_disposed) {
                    DisposeOwned();
                }
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void DisposeOwned() {
        try {
            _store?.Dispose();
            _control.Dispose();
            _cadence.Dispose();
            _timeline.Dispose();
        }
        finally {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }

    internal sealed class Operation : IDisposable {
        private GetterLifetime? _owner;
        private readonly GetterLifetime? _previous;
        internal Operation(GetterLifetime owner, GetterLifetime? previous) {
            _owner = owner;
            _previous = previous;
        }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)
            ?.Exit(_previous);
    }

    internal abstract record StoreOpen {
        private StoreOpen() { }
        internal sealed record Opened(RecapGridStoreReaderHandle Handle)
            : StoreOpen;
        internal sealed record Absent : StoreOpen;
        internal sealed record Busy : StoreOpen;
        internal sealed record Disposed : StoreOpen;
        internal sealed record UnsupportedSchema(int SchemaVersion)
            : StoreOpen;
        internal sealed record Invalid(string Code, string Detail)
            : StoreOpen;
    }
}
