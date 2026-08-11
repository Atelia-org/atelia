using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;
using System.Security.Cryptography;

namespace Atelia.SessionJournal.RecapGrid.Getter;

internal sealed class GetterLifetime : IDisposable {
    private readonly object _gate = new();
    private readonly HistoryTimelineReaderHandle _timeline;
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
        HistoryTimelineReaderHandle timeline,
        RecapGridControlReaderHandle control
    ) {
        _repositoryPath = repositoryPath;
        _timeline = timeline;
        _control = control;
    }

    internal HistoryTimelineReader Timeline => _timeline.Reader;
    internal RecapGridControlReader Control => _control.Reader;
    internal string OwnerNonce => _ownerNonce;

    internal Operation? TryEnter() {
        lock (_gate) {
            if (_closing) {
                return null;
            }
            _operations = checked(_operations + 1);
            return new Operation(this);
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
            _store?.Dispose();
            _control.Dispose();
            _timeline.Dispose();
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void Exit() {
        lock (_gate) {
            _operations--;
            if (_operations == 0) {
                Monitor.PulseAll(_gate);
            }
        }
    }

    internal sealed class Operation : IDisposable {
        private GetterLifetime? _owner;
        internal Operation(GetterLifetime owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
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
