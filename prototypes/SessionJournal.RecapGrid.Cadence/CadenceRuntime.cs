using Atelia.EventJournal;

namespace Atelia.SessionJournal.RecapGrid.Cadence;

public static class RecapGridCadenceFactory {
    public static RecapGridCadenceCreateResult Create(
        SessionJournalReadView readView,
        RecapGridCadencePolicySpec policy
    ) => CreateForTest(readView, policy,
        CadencePersistenceTestHooks.None);

    internal static RecapGridCadenceCreateResult CreateForTest(
        SessionJournalReadView readView,
        RecapGridCadencePolicySpec policy,
        CadencePersistenceTestHooks hooks
    ) {
        try {
            ArgumentNullException.ThrowIfNull(readView);
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(hooks);
            var paths = PathsFrom(readView);
            LinuxCadenceFiles.EnsureSlots(paths);
            using FileStream lease = LinuxCadenceFiles.AcquireLock(
                paths.LockPath, exclusive: true);
            if (LinuxCadenceFiles.EntryExists(paths.StatePath)) {
                RecapGridCadenceSnapshot existing = ReadExact(paths);
                return new RecapGridCadenceCreateResult.AlreadyExists(existing);
            }
            RecapGridCadenceSnapshot intended = CadenceCanonicalCodec.Create(
                paths.RefId, generation: 0, policy);
            try {
                LinuxCadenceFiles.WriteAtomic(paths,
                    intended.ToCanonicalBytes(), createNew: true, hooks);
            }
            catch (CadencePublishIndeterminateException) {
                return new RecapGridCadenceCreateResult.CommitIndeterminate(
                    intended.Head, ObserveHead(paths));
            }
            return new RecapGridCadenceCreateResult.Created(intended);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return MapCreate(exception);
        }
    }

    public static RecapGridCadenceOpenResult Open(
        SessionJournalReadView readView
    ) => OpenForTest(readView,
        CadencePersistenceTestHooks.None);

    internal static RecapGridCadenceOpenResult OpenForTest(
        SessionJournalReadView readView,
        CadencePersistenceTestHooks hooks
    ) {
        try {
            ArgumentNullException.ThrowIfNull(readView);
            ArgumentNullException.ThrowIfNull(hooks);
            var paths = PathsFrom(readView);
            return OpenCore(paths, hooks);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return MapOpen(exception);
        }
    }

    internal static RecapGridCadenceOpenResult OpenForMaintenance(
        string repositoryPath,
        RefId refId
    ) {
        try {
            var paths = new CadencePaths(repositoryPath, refId);
            ValidateRefForMaintenance(paths);
            return OpenCore(paths, CadencePersistenceTestHooks.None);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return MapOpen(exception);
        }
    }

    private static RecapGridCadenceOpenResult OpenCore(
        CadencePaths paths,
        CadencePersistenceTestHooks hooks
    ) {
            if (!Directory.Exists(paths.DirectoryPath)) {
                return new RecapGridCadenceOpenResult.Absent();
            }
            LinuxCadenceFiles.RequireExistingDirectoryChain(paths.DirectoryPath);
            if (!LinuxCadenceFiles.EntryExists(paths.StatePath)) {
                return new RecapGridCadenceOpenResult.Absent();
            }
            if (!LinuxCadenceFiles.EntryExists(paths.LockPath)) {
                return new RecapGridCadenceOpenResult.Invalid(
                    "CadenceLockAbsent",
                    "The canonical Cadence state exists without its lock slot.");
            }
            using (FileStream lease = LinuxCadenceFiles.AcquireLock(
                       paths.LockPath, exclusive: false)) {
                _ = ReadExact(paths);
            }
            var lifetime = new CadenceLifetime();
            var reader = new RecapGridCadenceReader(paths, lifetime);
            return new RecapGridCadenceOpenResult.Opened(
                new RecapGridCadenceHandle(
                    reader,
                    new RecapGridCadenceCoordinator(
                        paths, lifetime, hooks),
                    lifetime));
    }

    internal static RecapGridCadenceSnapshot ReadExact(CadencePaths paths) {
        RecapGridCadenceSnapshot snapshot = CadenceCanonicalCodec.Decode(
            LinuxCadenceFiles.ReadBounded(paths.StatePath));
        if (snapshot.Head.RefId != paths.RefId) {
            throw new CadenceStoreException(
                "CadenceRefMismatch",
                "The canonical Cadence state belongs to another Ref.");
        }
        return snapshot;
    }

    internal static RecapGridCadenceHeadRef? ObserveHead(CadencePaths paths) {
        try {
            return LinuxCadenceFiles.EntryExists(paths.StatePath)
                ? ReadExact(paths).Head
                : null;
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return null;
        }
    }

    private static CadencePaths PathsFrom(SessionJournalReadView readView) {
        // Reading both values through the same owner-bound view avoids opening a
        // second EventJournal owner and therefore remains valid while a mutable
        // SessionJournalEngine owns the repository.
        string repositoryPath = readView.Path;
        RefId refId = readView.BranchRefId;
        return new CadencePaths(repositoryPath, refId);
    }

    internal static void ValidateRefForMaintenance(CadencePaths paths) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(paths.RepositoryPath);
        _ = journal.GetHead(paths.RefId);
    }

    internal static RecapGridCadenceCreateResult MapCreate(Exception exception)
        => exception switch {
            PlatformNotSupportedException
                => new RecapGridCadenceCreateResult.PlatformUnsupported(),
            CadenceBusyException
                => new RecapGridCadenceCreateResult.Busy(),
            CadenceUnsupportedSchemaException schema
                => new RecapGridCadenceCreateResult.UnsupportedSchema(
                    schema.Version),
            CadenceStoreException invalid
                => new RecapGridCadenceCreateResult.Invalid(
                    invalid.Code, invalid.Message),
            _ => new RecapGridCadenceCreateResult.Invalid(
                "CadenceCreateInvalid", exception.Message)
        };

    internal static RecapGridCadenceOpenResult MapOpen(Exception exception)
        => exception switch {
            PlatformNotSupportedException
                => new RecapGridCadenceOpenResult.PlatformUnsupported(),
            CadenceBusyException
                => new RecapGridCadenceOpenResult.Busy(),
            CadenceUnsupportedSchemaException schema
                => new RecapGridCadenceOpenResult.UnsupportedSchema(
                    schema.Version),
            CadenceStoreException invalid
                => new RecapGridCadenceOpenResult.Invalid(
                    invalid.Code, invalid.Message),
            _ => new RecapGridCadenceOpenResult.Invalid(
                "CadenceOpenInvalid", exception.Message)
        };
}

public sealed class RecapGridCadenceHandle : IDisposable {
    private readonly CadenceLifetime _lifetime;
    internal RecapGridCadenceHandle(
        RecapGridCadenceReader reader,
        RecapGridCadenceCoordinator coordinator,
        CadenceLifetime lifetime
    ) {
        Reader = reader;
        Coordinator = coordinator;
        _lifetime = lifetime;
    }
    public RecapGridCadenceReader Reader { get; }
    public RecapGridCadenceCoordinator Coordinator { get; }
    public void Dispose() => _lifetime.Dispose();
}

public sealed class RecapGridCadenceReader {
    private readonly CadencePaths _paths;
    private readonly CadenceLifetime _lifetime;
    internal RecapGridCadenceReader(
        CadencePaths paths,
        CadenceLifetime lifetime
    ) {
        _paths = paths;
        _lifetime = lifetime;
    }

    public RecapGridCadenceReadResult ReadSnapshot() {
        using CadenceLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridCadenceReadResult.Disposed();
        }
        try {
            using FileStream lease = LinuxCadenceFiles.AcquireLock(
                _paths.LockPath, exclusive: false);
            return new RecapGridCadenceReadResult.Available(
                RecapGridCadenceFactory.ReadExact(_paths));
        }
        catch (CadenceBusyException) {
            return new RecapGridCadenceReadResult.Busy();
        }
        catch (CadenceUnsupportedSchemaException schema) {
            return new RecapGridCadenceReadResult.UnsupportedSchema(
                schema.Version);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return exception is CadenceStoreException invalid
                ? new RecapGridCadenceReadResult.Invalid(
                    invalid.Code, invalid.Message)
                : new RecapGridCadenceReadResult.Invalid(
                    "CadenceReadInvalid", exception.Message);
        }
    }
}

public sealed class RecapGridCadenceCoordinator {
    private readonly CadencePaths _paths;
    private readonly CadenceLifetime _lifetime;
    private readonly CadencePersistenceTestHooks _hooks;
    internal RecapGridCadenceCoordinator(
        CadencePaths paths,
        CadenceLifetime lifetime,
        CadencePersistenceTestHooks hooks
    ) {
        _paths = paths;
        _lifetime = lifetime;
        _hooks = hooks;
    }

    public RecapGridCadenceCompareExchangeResult CompareExchangePolicy(
        RecapGridCadenceHeadRef expected,
        RecapGridCadencePolicySpec policy
    ) {
        using CadenceLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridCadenceCompareExchangeResult.Disposed();
        }
        try {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(policy);
            if (expected.RefId != _paths.RefId) {
                return new RecapGridCadenceCompareExchangeResult.Invalid(
                    "CadenceExpectedRefMismatch",
                    "The expected Cadence head belongs to another Ref.");
            }
            using FileStream lease = LinuxCadenceFiles.AcquireLock(
                _paths.LockPath, exclusive: true);
            RecapGridCadenceSnapshot actual =
                RecapGridCadenceFactory.ReadExact(_paths);
            if (actual.Head != expected) {
                return new RecapGridCadenceCompareExchangeResult.Stale(
                    actual.Head);
            }
            RecapGridCadenceSnapshot desired = CadenceCanonicalCodec.Create(
                _paths.RefId,
                checked(actual.Head.Generation + 1),
                policy);
            if (desired.Head.DomainDigest == actual.Head.DomainDigest) {
                return new RecapGridCadenceCompareExchangeResult.Unchanged(
                    actual);
            }
            try {
                LinuxCadenceFiles.WriteAtomic(_paths,
                    desired.ToCanonicalBytes(), createNew: false, _hooks);
            }
            catch (CadencePublishIndeterminateException) {
                return new RecapGridCadenceCompareExchangeResult
                    .CommitIndeterminate(
                        desired.Head,
                        RecapGridCadenceFactory.ObserveHead(_paths));
            }
            return new RecapGridCadenceCompareExchangeResult.Updated(desired);
        }
        catch (CadenceBusyException) {
            return new RecapGridCadenceCompareExchangeResult.Busy();
        }
        catch (CadenceUnsupportedSchemaException schema) {
            return new RecapGridCadenceCompareExchangeResult
                .UnsupportedSchema(schema.Version);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return exception is CadenceStoreException invalid
                ? new RecapGridCadenceCompareExchangeResult.Invalid(
                    invalid.Code, invalid.Message)
                : new RecapGridCadenceCompareExchangeResult.Invalid(
                    "CadenceCompareExchangeInvalid", exception.Message);
        }
    }
}

internal sealed class CadenceLifetime : IDisposable {
    private readonly object _gate = new();
    private int _active;
    private bool _closing;
    private bool _disposed;

    internal Operation? TryEnter() {
        lock (_gate) {
            if (_closing) {
                return null;
            }
            _active++;
            return new Operation(this);
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _closing = true;
            while (_active != 0) {
                Monitor.Wait(_gate);
            }
            _disposed = true;
        }
    }

    private void Exit() {
        lock (_gate) {
            _active--;
            if (_closing && _active == 0) {
                Monitor.PulseAll(_gate);
            }
        }
    }

    internal sealed class Operation(CadenceLifetime owner) : IDisposable {
        private CadenceLifetime? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}
